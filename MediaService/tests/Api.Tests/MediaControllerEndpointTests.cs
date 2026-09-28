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
    }

    private static MultipartFormDataContent UploadForm(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "machine.jpg");
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
    public async Task Upload_WhenServiceTokenUsed_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetServiceToken(client);

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
    public async Task Upload_WhenValid_ShouldReturnReadyMedia()
    {
        var uploadService = Substitute.For<IMediaUploadService>();
        var dto = new MediaCompletedDto(Guid.NewGuid(), "Ready", "machine.jpg", "image/jpeg", 3,
            "https://storage.test/preview", DateTimeOffset.UtcNow.AddMinutes(5));

        uploadService.UploadAsync(
            "user-123",
            "key-123",
            Arg.Is<UploadMediaRequest>(request => request.FileName == "machine.jpg" &&
                request.ContentType == "image/jpeg" && request.SizeBytes == 3),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(dto));

        using var factory = new MediaServiceTestFactory { MediaUploadServiceOverride = uploadService };
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, "user-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-123");

        using var form = UploadForm([0xFF, 0xD8, 0xFF]);
        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.NotNull(body);
        Assert.Equal(dto.MediaId, body.MediaId);
        Assert.Equal("Ready", body.Status);
        Assert.Equal(dto.PreviewUrl, body.PreviewUrl);
    }

    [Fact]
    public async Task OldCompleteRoute_IsRemoved()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/v1/media/uploads/{Guid.NewGuid()}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VerifyMedia_WhenNoServiceAuth_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/media/{Guid.NewGuid()}/verify");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task VerifyMedia_WhenServiceAuthenticated_ShouldReturn200()
    {
        var mediaId = Guid.NewGuid();
        var accessService = Substitute.For<IMediaAccessService>();
        var dto = new MediaVerificationDto(
            mediaId,
            "Ready",
            "user-123",
            "machine.jpg",
            "image/jpeg",
            5000,
            DateTimeOffset.UtcNow);

        accessService.VerifyMediaAsync(
                Arg.Is<ServiceCaller>(caller => caller.Id == "11111111-1111-1111-1111-111111111111" && caller.HasScope("media.verify")),
                mediaId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(dto));

        using var factory = new MediaServiceTestFactory { MediaAccessServiceOverride = accessService };
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetServiceToken(client);

        var response = await client.GetAsync($"/api/v1/media/{mediaId}/verify");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MediaVerificationDto>();
        Assert.NotNull(body);
        Assert.Equal(mediaId, body.MediaId);
        Assert.Equal("Ready", body.Status);
        Assert.Equal("user-123", body.UploadedByUserId);
    }

    [Fact]
    public async Task InternalReadAndVisibility_RejectMissingAuthOrScope()
    {
        using var factory = new MediaServiceTestFactory();
        using var anonymous = factory.CreateClient();
        Guid mediaId = Guid.NewGuid();
        var readRequest = new MediaAccessRequest("read");
        var visibilityRequest = new ChangeMediaVisibilityRequest("public");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/v1/media/{mediaId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"/api/v1/media/{mediaId}/access", readRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync($"/api/v1/media/{mediaId}/visibility", visibilityRequest)).StatusCode);

        using var verifyOnly = factory.CreateClient();
        MediaServiceTestFactory.SetServiceToken(verifyOnly, "media.verify");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await verifyOnly.GetAsync($"/api/v1/media/{mediaId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await verifyOnly.PostAsJsonAsync($"/api/v1/media/{mediaId}/access", readRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await verifyOnly.PutAsJsonAsync($"/api/v1/media/{mediaId}/visibility", visibilityRequest)).StatusCode);

        using var user = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(user, "user-1");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await user.GetAsync($"/api/v1/media/{mediaId}")).StatusCode);
    }
}
