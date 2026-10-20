using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common;
using NSubstitute;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaControllerEndpointTests
{
    [Fact]
    public async Task Swagger_DescribesDirectMultipartUpload()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement upload = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/media/uploads").GetProperty("post");
        JsonElement multipart = upload.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data");
        Assert.True(multipart.TryGetProperty("schema", out _));
        Assert.DoesNotContain("ownerEntityId", multipart.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("purpose", multipart.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.False(document.RootElement.GetProperty("paths")
            .TryGetProperty("/api/v1/media/uploads/{uploadId}/complete", out _));
        foreach ((string path, string method) in new[]
        {
            ("/api/v1/media/{mediaId}/verify", "get"),
            ("/api/v1/media/{mediaId}", "get"),
            ("/api/v1/media/{mediaId}/access", "post"),
            ("/api/v1/media/{mediaId}/visibility", "put")
        })
        {
            JsonElement operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "X-Api-Key");
        }
    }

    private static MultipartFormDataContent UploadForm(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "files", "machine.jpg");
        return form;
    }

    [Fact]
    public async Task Upload_WhenNoAuthHeader_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        using var form = UploadForm([0xFF, 0xD8, 0xFF]);

        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenIdentityHeaderIsSpoofed_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "attacker");

        using var form = UploadForm([0xFF, 0xD8, 0xFF]);
        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenApiKeyOnlyUsed_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(client);

        using var form = UploadForm([0xFF, 0xD8, 0xFF]);
        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task VerifyMedia_WhenServiceHeaderIsSpoofed_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Name", "CatalogService");

        var response = await client.GetAsync($"/api/v1/media/{Guid.NewGuid()}/verify");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenFileMissing_ShouldReturn400()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, "user-123");

        using var form = new MultipartFormDataContent();
        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenChecksumCountDoesNotMatchFileCount_ShouldReturn400()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, "user-123");
        using var form = UploadForm([0xFF, 0xD8, 0xFF]);
        var second = new ByteArrayContent([0xFF, 0xD8, 0xFF]);
        second.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(second, "files", "second.jpg");
        form.Add(new StringContent(new string('0', 64)), "sha256Checksums");

        using var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenMoreThanTenFiles_ShouldReturn400()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, "user-123");
        using var form = new MultipartFormDataContent();
        for (int index = 0; index < 11; index++)
        {
            var file = new ByteArrayContent([0xFF, 0xD8, 0xFF]);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "files", $"file-{index}.jpg");
        }

        using var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenValid_ShouldReturnReadyMedia()
    {
        var uploadService = Substitute.For<IMediaUploadService>();
        var dto = new MediaCompletedDto(Guid.NewGuid(), "Ready", "machine.jpg", "image/jpeg", 3,
            "https://storage.test/preview", DateTimeOffset.UtcNow.AddMinutes(5));

        uploadService.UploadManyAsync(
            "user-123",
            "key-123",
            Arg.Is<IReadOnlyList<UploadMediaRequest>>(requests => requests.Count == 1 &&
                requests[0].FileName == "machine.jpg" && requests[0].ContentType == "image/jpeg" &&
                requests[0].SizeBytes == 3),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<UploadFileResultDto>>(
                [new UploadFileResultDto(0, "machine.jpg", dto, null)]));

        using var factory = new MediaServiceTestFactory { MediaUploadServiceOverride = uploadService };
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, "user-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-123");

        using var form = UploadForm([0xFF, 0xD8, 0xFF]);
        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<UploadFileResultDto[]>();
        Assert.NotNull(body);
        Assert.Single(body);
        Assert.Equal(dto.MediaId, body[0].Media?.MediaId);
        Assert.Equal("Ready", body[0].Media?.Status);
        Assert.Equal(dto.PreviewUrl, body[0].Media?.PreviewUrl);
    }

    [Fact]
    public async Task OldCompleteRoute_IsRemoved()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(client);

        var response = await client.PostAsync($"/api/v1/media/uploads/{Guid.NewGuid()}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/media/{0}/verify", "GET")]
    [InlineData("/api/v1/media/{0}", "GET")]
    [InlineData("/api/v1/media/{0}/access", "POST")]
    [InlineData("/api/v1/media/{0}/visibility", "PUT")]
    public async Task InternalEndpoints_WhenApiKeyMissingOrWrong_ShouldReturn401(string routeTemplate, string method)
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        Guid mediaId = Guid.NewGuid();
        string path = string.Format(System.Globalization.CultureInfo.InvariantCulture, routeTemplate, mediaId);

        // 1. Không có header X-Api-Key -> 401
        var missingKeyResponse = await SendInternalRequest(client, method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, missingKeyResponse.StatusCode);

        // 2. Sai X-Api-Key -> 401
        using var wrongKeyClient = factory.CreateClient();
        wrongKeyClient.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");
        var wrongKeyResponse = await SendInternalRequest(wrongKeyClient, method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKeyResponse.StatusCode);

        // 3. User JWT không thay thế được key -> 401
        using var userJwtClient = factory.CreateClient();
        MediaServiceTestFactory.SetUserTokenOnly(userJwtClient, "user-123");
        var userJwtResponse = await SendInternalRequest(userJwtClient, method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, userJwtResponse.StatusCode);

        // 4. User JWT kèm sai key -> 401
        userJwtClient.DefaultRequestHeaders.Add("X-Api-Key", "wrong-api-key");
        var userJwtWrongKeyResponse = await SendInternalRequest(userJwtClient, method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, userJwtWrongKeyResponse.StatusCode);
    }

    [Fact]
    public async Task InternalEndpoints_WhenValidApiKey_ShouldSucceed()
    {
        var mediaId = Guid.NewGuid();
        var accessService = Substitute.For<IMediaAccessService>();
        accessService.VerifyMediaAsync(mediaId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new MediaVerificationDto(
                mediaId, "Ready", "user-123", "machine.jpg", "image/jpeg", 5000, DateTimeOffset.UtcNow)));
        accessService.GetMetadataAsync(mediaId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(new MediaMetadataDto(
                mediaId, "machine.jpg", "image/jpeg", 5000, "Ready", "Private", DateTimeOffset.UtcNow)));
        accessService.CreatePrivateReadUrlAsync(mediaId, Arg.Any<MediaAccessRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new MediaReadUrlDto(mediaId, "https://storage.test/read", DateTimeOffset.UtcNow.AddMinutes(5))));
        accessService.SetVisibilityAsync(mediaId, Arg.Any<ChangeMediaVisibilityRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new MediaVisibilityDto(mediaId, "Public")));

        using var factory = new MediaServiceTestFactory { MediaAccessServiceOverride = accessService };
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(client);

        var verifyResp = await client.GetAsync($"/api/v1/media/{mediaId}/verify");
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        var metaResp = await client.GetAsync($"/api/v1/media/{mediaId}");
        Assert.Equal(HttpStatusCode.OK, metaResp.StatusCode);

        var accessResp = await client.PostAsJsonAsync($"/api/v1/media/{mediaId}/access", new MediaAccessRequest("read"));
        Assert.Equal(HttpStatusCode.OK, accessResp.StatusCode);

        var visResp = await client.PutAsJsonAsync($"/api/v1/media/{mediaId}/visibility", new ChangeMediaVisibilityRequest("public"));
        Assert.Equal(HttpStatusCode.OK, visResp.StatusCode);
    }

    [Fact]
    public async Task Upload_RequiresUserJwt_EvenIfApiKeyPresent()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(client);

        using var form = new MultipartFormDataContent();
        var response = await client.PostAsync("/api/v1/media/uploads", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendInternalRequest(HttpClient client, string method, string path)
    {
        return method switch
        {
            "GET" => await client.GetAsync(path),
            "POST" => await client.PostAsJsonAsync(path, new MediaAccessRequest("read")),
            "PUT" => await client.PutAsJsonAsync(path, new ChangeMediaVisibilityRequest("public")),
            _ => throw new NotSupportedException(method)
        };
    }
}
