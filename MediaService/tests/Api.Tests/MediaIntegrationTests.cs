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
    private static byte[] ValidJpeg(int width = 100, int height = 100)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    private static MultipartFormDataContent Form(byte[] bytes, string contentType = "image/jpeg", string fileName = "excavator.jpg")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "files", fileName);
        return form;
    }

    [Fact]
    public async Task MultipartUpload_MultipleFilesReturnIndependentResultsAndReplaySuccessfulFiles()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        using var form = Form(ValidJpeg(), fileName: "valid.jpg");
        var invalid = new ByteArrayContent([0x00, 0x01, 0x02]);
        invalid.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(invalid, "files", "invalid.jpg");
        using var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        UploadFileResultDto[] items = (await response.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!;
        Assert.Equal(2, items.Length);
        Assert.Equal(0, items[0].Index);
        Assert.Equal("Ready", items[0].Media?.Status);
        Assert.Null(items[0].Error);
        Assert.Equal(1, items[1].Index);
        Assert.Null(items[1].Media);
        Assert.NotNull(items[1].Error);

        using var sameBatch = Form(ValidJpeg(), fileName: "valid.jpg");
        var sameInvalid = new ByteArrayContent([0x00, 0x01, 0x02]);
        sameInvalid.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        sameBatch.Add(sameInvalid, "files", "invalid.jpg");
        using var sameResponse = await client.PostAsync("/api/v1/media/uploads", sameBatch);
        var sameItems = (await sameResponse.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!;
        Assert.Equal(items[0].Media?.MediaId, sameItems[0].Media?.MediaId);
        Assert.NotNull(sameItems[1].Error);

        using var replay = Form(ValidJpeg(), fileName: "valid.jpg");
        using var replayResponse = await client.PostAsync("/api/v1/media/uploads", replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = Assert.Single((await replayResponse.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!);
        Assert.Equal("IdempotencyConflict", replayed.Error?.Code);
    }

    [Fact]
    public async Task MultipartUpload_ChecksumFailureDoesNotDiscardOtherReadyFile()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        byte[] jpeg = ValidJpeg();
        using var form = Form(jpeg, fileName: "matching.jpg");
        var second = new ByteArrayContent(jpeg);
        second.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(second, "files", "mismatch.jpg");
        form.Add(new StringContent(Convert.ToHexString(SHA256.HashData(jpeg))), "sha256Checksums");
        form.Add(new StringContent(new string('0', 64)), "sha256Checksums");

        using var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await response.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!;
        Assert.Equal(2, items.Length);
        Assert.Equal("Ready", items[0].Media?.Status);
        Assert.Null(items[0].Error);
        Assert.Null(items[1].Media);
        Assert.Equal("ChecksumMismatch", items[1].Error?.Code);
    }

    [Fact]
    public async Task Pdf_CanBePublishedByScopedService()
    {
        using var factory = new MediaServiceTestFactory();
        using var userClient = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(userClient, $"user-{Guid.NewGuid():N}");
        byte[] pdf = "%PDF-1.7\nMVP file"u8.ToArray();
        using var form = Form(pdf, "application/pdf", "document.pdf");

        using var upload = await userClient.PostAsync("/api/v1/media/uploads", form);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var media = Assert.Single((await upload.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!).Media;
        Assert.NotNull(media);
        Assert.Equal("application/pdf", media.ContentType);

        using var serviceClient = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(serviceClient);
        using var publish = await serviceClient.PutAsJsonAsync($"/api/v1/media/{media.MediaId}/visibility",
            new ChangeMediaVisibilityRequest("public"));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var publicResponse = await anonymous.GetAsync($"/api/v1/media/{media.MediaId}/content");
        Assert.Equal(HttpStatusCode.Redirect, publicResponse.StatusCode);
        Assert.NotNull(publicResponse.Headers.Location);
        using var storageClient = new HttpClient();
        using var publicDownload = await storageClient.GetAsync(publicResponse.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, publicDownload.StatusCode);
        Assert.Equal(pdf, await publicDownload.Content.ReadAsByteArrayAsync());
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
        var uploaded = Assert.Single((await uploadResponse.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!).Media;
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
        byte[] previewBytes = await previewDownload.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(previewBytes);
        using var previewBitmap = SkiaSharp.SKBitmap.Decode(previewBytes);
        Assert.NotNull(previewBitmap);
        string uploadJson = await uploadResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("uploadUrl", uploadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uploadId", uploadJson, StringComparison.OrdinalIgnoreCase);

        using var replayForm = Form(jpeg);
        var replayResponse = await userClient.PostAsync("/api/v1/media/uploads", replayForm);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = Assert.Single((await replayResponse.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!).Media;
        Assert.Equal(uploaded.MediaId, replayed?.MediaId);
        Assert.False(string.IsNullOrWhiteSpace(replayed?.PreviewUrl));

        using var serviceClient = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(serviceClient);
        var verifyResponse = await serviceClient.GetAsync($"/api/v1/media/{uploaded.MediaId}/verify");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verified = await verifyResponse.Content.ReadFromJsonAsync<MediaVerificationDto>();
        Assert.Equal("image/jpeg", verified?.ContentType);
        Assert.NotNull(verified?.UploadedByUserId);

        var metadataResponse = await serviceClient.GetAsync($"/api/v1/media/{uploaded.MediaId}");
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<MediaMetadataDto>();
        Assert.Equal("Private", metadata?.Visibility);
        Assert.Equal(jpeg.Length, metadata?.SizeBytes);
        string metadataJson = await metadataResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageKey", metadataJson, StringComparison.OrdinalIgnoreCase);

        var accessResponse = await serviceClient.PostAsJsonAsync($"/api/v1/media/{uploaded.MediaId}/access",
            new MediaAccessRequest("read", true));
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
            new ChangeMediaVisibilityRequest("public"));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var publicResponse = await anonymous.GetAsync($"/api/v1/media/{uploaded.MediaId}/content?variant=original");
        Assert.Equal(HttpStatusCode.Redirect, publicResponse.StatusCode);
        Assert.NotNull(publicResponse.Headers.Location);
        using var publicDownload = await storageClient.GetAsync(publicResponse.Headers.Location);
        Assert.Equal(jpeg, await publicDownload.Content.ReadAsByteArrayAsync());

        var unpublish = await serviceClient.PutAsJsonAsync($"/api/v1/media/{uploaded.MediaId}/visibility",
            new ChangeMediaVisibilityRequest("private"));
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
        string userId = $"user-{Guid.NewGuid():N}";
        MediaServiceTestFactory.SetUserToken(client, userId);
        string key = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var form = Form([0x00, 0x01, 0x02, 0x03]);

        var response = await client.PostAsync("/api/v1/media/uploads", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!);
        Assert.Null(item.Media);
        Assert.NotNull(item.Error);
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var session = await db.MediaUploadSessions.Include(s => s.MediaAsset)
            .SingleAsync(s => s.CallerIdentity == userId);
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

        using var firstForm = Form(ValidJpeg());
        using var secondForm = Form(ValidJpeg());
        var results = await Task.WhenAll(
            firstClient.PostAsync("/api/v1/media/uploads", firstForm),
            secondClient.PostAsync("/api/v1/media/uploads", secondForm));
        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.StatusCode));

        using var replayForm = Form(ValidJpeg());
        var replayResponse = await firstClient.PostAsync("/api/v1/media/uploads", replayForm);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replayed = Assert.Single((await replayResponse.Content.ReadFromJsonAsync<UploadFileResultDto[]>())!).Media;
        Assert.NotNull(replayed);
        using IServiceScope scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        Assert.Equal(1, await db.MediaUploadSessions.CountAsync(s => s.CallerIdentity == userId));
        Assert.Equal(1, await db.MediaAssets.CountAsync(a => a.Id == replayed.MediaId));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}
