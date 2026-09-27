using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain;
using Dprental.MediaService.Domain.IStorage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

[Collection("Integration")]
public class MediaIntegrationTests
{
    private static byte[] ValidJpeg(int size = 1024)
    {
        byte[] bytes = new byte[size];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        bytes[4] = 0x00;
        bytes[5] = 0x10;
        bytes[6] = 0x4A;
        bytes[7] = 0x46;
        return bytes;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string ownerId = "equipment-test-1")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "excavator.jpg");
        form.Add(new StringContent("equipment-image"), "purpose");
        form.Add(new StringContent(ownerId), "ownerEntityId");
        return form;
    }

    [Fact]
    public async Task MultipartUpload_EndToEnd_ReturnsReadyAndSupportsReadAndPublicAccess()
    {
        using var factory = new MediaServiceTestFactory();
        using var userClient = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(userClient, $"user-{Guid.NewGuid():N}");
        string idempotencyKey = Guid.NewGuid().ToString("N");
        userClient.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);
        byte[] jpeg = ValidJpeg();

        using var uploadForm = Form(jpeg);
        var uploadResponse = await userClient.PostAsync("/api/v1/media/uploads", uploadForm);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.NotNull(uploaded);
        Assert.Equal("Ready", uploaded.Status);
        Assert.Equal("excavator.jpg", uploaded.OriginalFileName);
        Assert.Equal("image/jpeg", uploaded.ContentType);
        Assert.Equal(jpeg.Length, uploaded.SizeBytes);
        Assert.False(string.IsNullOrWhiteSpace(uploaded.PreviewUrl));
        Assert.True(uploaded.PreviewUrlExpiresAtUtc > DateTimeOffset.UtcNow);
        using var previewClient = new HttpClient();
        using var previewDownload = await previewClient.GetAsync(uploaded.PreviewUrl);
        Assert.Equal(HttpStatusCode.OK, previewDownload.StatusCode);
        Assert.Equal(jpeg, await previewDownload.Content.ReadAsByteArrayAsync());
        string uploadJson = await uploadResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("uploadUrl", uploadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uploadId", uploadJson, StringComparison.OrdinalIgnoreCase);

        using var replayForm = Form(jpeg);
        var replayResponse = await userClient.PostAsync("/api/v1/media/uploads", replayForm);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = await replayResponse.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.Equal(uploaded.MediaId, replayed?.MediaId);
        Assert.False(string.IsNullOrWhiteSpace(replayed?.PreviewUrl));

        using var serviceClient = factory.CreateClient();
        MediaServiceTestFactory.SetServiceToken(serviceClient, "media.verify", "media.read", "media.publish");
        var verifyResponse = await serviceClient.GetAsync($"/api/v1/media/{uploaded.MediaId}/verify");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verified = await verifyResponse.Content.ReadFromJsonAsync<MediaVerificationDto>();
        Assert.Equal("CatalogService", verified?.OwnerService);
        Assert.Equal("equipment-test-1", verified?.OwnerEntityId);

        var metadataResponse = await serviceClient.GetAsync($"/api/v1/media/{uploaded.MediaId}");
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<MediaMetadataDto>();
        Assert.Equal("Private", metadata?.Visibility);
        Assert.Equal(jpeg.Length, metadata?.SizeBytes);
        string metadataJson = await metadataResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", metadataJson, StringComparison.OrdinalIgnoreCase);

        var denied = await serviceClient.PostAsJsonAsync($"/api/v1/media/{uploaded.MediaId}/access",
            new MediaAccessRequest("read", "Equipment", "wrong-equipment"));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        var accessResponse = await serviceClient.PostAsJsonAsync($"/api/v1/media/{uploaded.MediaId}/access",
            new MediaAccessRequest("read", "Equipment", "equipment-test-1", true));
        Assert.Equal(HttpStatusCode.OK, accessResponse.StatusCode);
        var access = await accessResponse.Content.ReadFromJsonAsync<MediaReadUrlDto>();
        Assert.NotNull(access);
        Assert.Contains("X-Amz-Expires=300", access.Url, StringComparison.Ordinal);
        using var storageClient = new HttpClient();
        using var downloaded = await storageClient.GetAsync(access.Url);
        Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
        Assert.Equal(jpeg, await downloaded.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("excavator.jpg", downloaded.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var publish = await serviceClient.PutAsJsonAsync($"/api/v1/media/{uploaded.MediaId}/visibility",
            new ChangeMediaVisibilityRequest("public", "Equipment", "equipment-test-1"));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var publicResponse = await anonymous.GetAsync($"/api/v1/media/{uploaded.MediaId}/content");
        Assert.Equal(HttpStatusCode.Redirect, publicResponse.StatusCode);
        Assert.NotNull(publicResponse.Headers.Location);
        using var publicDownload = await storageClient.GetAsync(publicResponse.Headers.Location);
        Assert.Equal(jpeg, await publicDownload.Content.ReadAsByteArrayAsync());

        var unpublish = await serviceClient.PutAsJsonAsync($"/api/v1/media/{uploaded.MediaId}/visibility",
            new ChangeMediaVisibilityRequest("private", "Equipment", "equipment-test-1"));
        Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode);
        using var stopped = await anonymous.GetAsync($"/api/v1/media/{uploaded.MediaId}/content");
        Assert.Equal(HttpStatusCode.NotFound, stopped.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var asset = await db.MediaAssets.SingleAsync(a => a.Id == uploaded.MediaId);
        Assert.Equal(MediaStatus.Ready, asset.Status);
        Assert.Equal(MediaVisibility.Private, asset.Visibility);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant(), asset.ActualChecksumSha256);
        await using Stream? storedObject = await scope.ServiceProvider.GetRequiredService<IObjectStorageService>()
            .GetObjectStreamAsync(asset.StorageKey!);
        Assert.NotNull(storedObject);
        Assert.Equal(jpeg, await ReadAllAsync(storedObject));
    }

    [Fact]
    public async Task MultipartUpload_InvalidFileIsRejectedAndCleanupScheduled()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        string key = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var form = Form([0x00, 0x01, 0x02, 0x03]);

        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var session = await db.MediaUploadSessions.Include(s => s.MediaAsset)
            .SingleAsync(s => s.IdempotencyKey == key);
        Assert.Equal(UploadSessionStatus.Rejected, session.Status);
        Assert.Equal(MediaStatus.Rejected, session.MediaAsset?.Status);
        Assert.True(await db.MediaCleanupItems.AnyAsync(item => item.StorageKey == session.TemporaryStorageKey));
    }

    [Fact]
    public async Task MultipartUpload_SameKeyConcurrentRequestsCreateOneMedia()
    {
        using var factory = new MediaServiceTestFactory();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        string userId = $"user-{Guid.NewGuid():N}";
        string key = Guid.NewGuid().ToString("N");
        foreach (HttpClient client in new[] { firstClient, secondClient })
        {
            MediaServiceTestFactory.SetUserToken(client, userId);
            client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        }

        using var firstForm = Form(ValidJpeg(), "eq-concurrent");
        using var secondForm = Form(ValidJpeg(), "eq-concurrent");
        var results = await Task.WhenAll(
            firstClient.PostAsync("/api/v1/media/uploads", firstForm),
            secondClient.PostAsync("/api/v1/media/uploads", secondForm));
        Assert.All(results, result => Assert.Contains(result.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        using var replayForm = Form(ValidJpeg(), "eq-concurrent");
        var replayResponse = await firstClient.PostAsync("/api/v1/media/uploads", replayForm);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = await replayResponse.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.NotNull(replayed);
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        Assert.Equal(1, await db.MediaUploadSessions.CountAsync(s => s.CallerIdentity == userId && s.IdempotencyKey == key));
        Assert.Equal(1, await db.MediaAssets.CountAsync(a => a.Id == replayed.MediaId));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}
