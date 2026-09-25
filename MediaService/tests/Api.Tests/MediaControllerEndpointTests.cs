using System.Net;
using System.Net.Http.Json;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common;
using NSubstitute;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

public class MediaControllerEndpointTests
{
    [Fact]
    public async Task InitializeUpload_WhenNoAuthHeader_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);
        var response = await client.PostAsJsonAsync("/api/v1/media/uploads", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InitializeUpload_WhenAuthPresentButNoIdempotencyKey_ShouldReturn400()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-123");

        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);
        var response = await client.PostAsJsonAsync("/api/v1/media/uploads", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InitializeUpload_WhenValid_ShouldReturn200()
    {
        var uploadService = Substitute.For<IMediaUploadService>();
        var dto = new UploadSessionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "http://minio/upload",
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" },
            DateTimeOffset.UtcNow.AddMinutes(15));

        uploadService.InitializeUploadAsync(
            "user-123",
            "key-123",
            Arg.Any<InitializeUploadRequest>(),
            Arg.Any<CancellationToken>())
            .Returns(Result.Success(dto));

        using var factory = new MediaServiceTestFactory { MediaUploadServiceOverride = uploadService };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-123");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-123");

        var request = new InitializeUploadRequest("equipment-image", "eq-1", "file.jpg", "image/jpeg", 1000);
        var response = await client.PostAsJsonAsync("/api/v1/media/uploads", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UploadSessionDto>();
        Assert.NotNull(body);
        Assert.Equal(dto.MediaId, body.MediaId);
        Assert.Equal(dto.UploadId, body.UploadId);
    }

    [Fact]
    public async Task CompleteUpload_WhenNoAuthHeader_ShouldReturn401()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/v1/media/uploads/{Guid.NewGuid()}/complete", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CompleteUpload_WhenValid_ShouldReturn200()
    {
        var uploadId = Guid.NewGuid();
        var uploadService = Substitute.For<IMediaUploadService>();
        var dto = new MediaCompletedDto(
            Guid.NewGuid(),
            "Ready",
            "test.jpg",
            "image/jpeg",
            2048);

        uploadService.CompleteUploadAsync("user-123", uploadId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(dto));

        using var factory = new MediaServiceTestFactory { MediaUploadServiceOverride = uploadService };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-123");

        var response = await client.PostAsync($"/api/v1/media/uploads/{uploadId}/complete", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.NotNull(body);
        Assert.Equal(dto.MediaId, body.MediaId);
        Assert.Equal("Ready", body.Status);
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
        var uploadService = Substitute.For<IMediaUploadService>();
        var dto = new MediaVerificationDto(
            mediaId,
            "Ready",
            "CatalogService",
            "Equipment",
            "eq-100",
            "equipment-image",
            "user-123",
            "machine.jpg",
            "image/jpeg",
            5000,
            DateTimeOffset.UtcNow);

        uploadService.VerifyMediaAsync("CatalogService", mediaId, Arg.Any<CancellationToken>())
            .Returns(Result.Success(dto));

        using var factory = new MediaServiceTestFactory { MediaUploadServiceOverride = uploadService };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Name", "CatalogService");

        var response = await client.GetAsync($"/api/v1/media/{mediaId}/verify");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MediaVerificationDto>();
        Assert.NotNull(body);
        Assert.Equal(mediaId, body.MediaId);
        Assert.Equal("Ready", body.Status);
        Assert.Equal("CatalogService", body.OwnerService);
    }
}
