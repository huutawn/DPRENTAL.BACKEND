using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

[Collection("Integration")]
public class MediaIntegrationTests
{
    private static byte[] CreateValidJpegBytes(int size = 1024)
    {
        byte[] bytes = new byte[size];
        // JPEG magic header: FF D8 FF E0 00 10 4A 46
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        bytes[4] = 0x00;
        bytes[5] = 0x10;
        bytes[6] = 0x4A;
        bytes[7] = 0x46;
        for (int i = 8; i < size; i++)
        {
            bytes[i] = (byte)(i % 256);
        }
        return bytes;
    }

    [Fact]
    public async Task DirectUploadFlow_EndToEnd_ShouldSucceedAndStoreMetadata()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        string userId = $"user-{Guid.NewGuid():N}";
        string idempotencyKey = Guid.NewGuid().ToString("N");
        byte[] jpegBytes = CreateValidJpegBytes(2048);
        string sha256Hex = Convert.ToHexString(SHA256.HashData(jpegBytes)).ToLowerInvariant();

        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        // 1. Khởi tạo phiên upload
        var initRequest = new InitializeUploadRequest(
            Purpose: "equipment-image",
            OwnerEntityId: "equipment-test-1",
            FileName: "excavator.jpg",
            ContentType: "image/jpeg",
            SizeBytes: jpegBytes.Length,
            Sha256Checksum: sha256Hex);

        var initRes = await client.PostAsJsonAsync("/api/v1/media/uploads", initRequest);
        Assert.Equal(HttpStatusCode.OK, initRes.StatusCode);

        var sessionDto = await initRes.Content.ReadFromJsonAsync<UploadSessionDto>();
        Assert.NotNull(sessionDto);
        Assert.NotEqual(Guid.Empty, sessionDto.MediaId);
        Assert.NotEqual(Guid.Empty, sessionDto.UploadId);
        Assert.NotNull(sessionDto.UploadUrl);

        // 2. Client tải binary trực tiếp lên MinIO bằng URL ký sẵn (PUT)
        using var minioClient = new HttpClient();
        using var content = new ByteArrayContent(jpegBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var putRes = await minioClient.PutAsync(sessionDto.UploadUrl, content);
        Assert.True(putRes.IsSuccessStatusCode, $"Tải binary lên MinIO thất bại với mã {putRes.StatusCode}");

        // 3. Client gọi Complete phiên upload
        var completeRes = await client.PostAsync($"/api/v1/media/uploads/{sessionDto.UploadId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        var completedDto = await completeRes.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.NotNull(completedDto);
        Assert.Equal(sessionDto.MediaId, completedDto.MediaId);
        Assert.Equal("Ready", completedDto.Status);
        Assert.Equal("excavator.jpg", completedDto.OriginalFileName);
        Assert.Equal("image/jpeg", completedDto.ContentType);
        Assert.Equal(jpegBytes.Length, completedDto.SizeBytes);

        // 4. Gọi lại Complete lần 2 -> Trả cùng kết quả (Idempotent)
        var repeatRes = await client.PostAsync($"/api/v1/media/uploads/{sessionDto.UploadId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, repeatRes.StatusCode);
        var repeatDto = await repeatRes.Content.ReadFromJsonAsync<MediaCompletedDto>();
        Assert.NotNull(repeatDto);
        Assert.Equal(completedDto.MediaId, repeatDto.MediaId);
        Assert.Equal("Ready", repeatDto.Status);

        // 5. Backend nghiệp vụ gọi verify MediaId bằng service token
        using var serviceClient = factory.CreateClient();
        serviceClient.DefaultRequestHeaders.Add("X-Service-Name", "CatalogService");

        var verifyRes = await serviceClient.GetAsync($"/api/v1/media/{sessionDto.MediaId}/verify");
        Assert.Equal(HttpStatusCode.OK, verifyRes.StatusCode);

        var verifyDto = await verifyRes.Content.ReadFromJsonAsync<MediaVerificationDto>();
        Assert.NotNull(verifyDto);
        Assert.Equal(sessionDto.MediaId, verifyDto.MediaId);
        Assert.Equal("Ready", verifyDto.Status);
        Assert.Equal("CatalogService", verifyDto.OwnerService);
        Assert.Equal("Equipment", verifyDto.OwnerEntityType);
        Assert.Equal("equipment-test-1", verifyDto.OwnerEntityId);
        Assert.Equal("equipment-image", verifyDto.Purpose);
        Assert.Equal(userId, verifyDto.UploadedByUserId);

        // 6. Kiểm tra trực tiếp trong PostgreSQL
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();

        var assetInDb = await db.MediaAssets.FirstOrDefaultAsync(a => a.Id == sessionDto.MediaId);
        Assert.NotNull(assetInDb);
        Assert.Equal(MediaStatus.Ready, assetInDb.Status);
        Assert.NotNull(assetInDb.StorageKey);
        Assert.StartsWith("media/", assetInDb.StorageKey);
        Assert.Equal(sha256Hex, assetInDb.ActualChecksumSha256);
    }

    [Fact]
    public async Task CompleteUpload_WhenNotUploadedYet_ShouldReturn400AndKeepPending()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        string userId = $"user-{Guid.NewGuid():N}";
        string idempotencyKey = Guid.NewGuid().ToString("N");

        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var initRequest = new InitializeUploadRequest(
            Purpose: "equipment-image",
            OwnerEntityId: "equipment-test-not-uploaded",
            FileName: "not-yet.jpg",
            ContentType: "image/jpeg",
            SizeBytes: 1000);

        var initRes = await client.PostAsJsonAsync("/api/v1/media/uploads", initRequest);
        var sessionDto = await initRes.Content.ReadFromJsonAsync<UploadSessionDto>();
        Assert.NotNull(sessionDto);

        // Gọi Complete NGAY mà chưa tải binary lên MinIO
        var completeRes = await client.PostAsync($"/api/v1/media/uploads/{sessionDto.UploadId}/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, completeRes.StatusCode);

        // Kiểm tra database: vẫn ở trạng thái PendingUpload và Pending
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
        var session = await db.MediaUploadSessions.Include(s => s.MediaAsset).FirstOrDefaultAsync(s => s.Id == sessionDto.UploadId);
        Assert.NotNull(session);
        Assert.Equal(UploadSessionStatus.Pending, session.Status);
        Assert.Equal(MediaStatus.PendingUpload, session.MediaAsset!.Status);
    }

    [Fact]
    public async Task CompleteUpload_WhenFileRejected_ShouldMarkRejectedAndCleanupObject()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        string userId = $"user-{Guid.NewGuid():N}";
        string idempotencyKey = Guid.NewGuid().ToString("N");

        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var initRequest = new InitializeUploadRequest(
            Purpose: "equipment-image",
            OwnerEntityId: "equipment-test-reject",
            FileName: "fake.jpg",
            ContentType: "image/jpeg",
            SizeBytes: 500);

        var initRes = await client.PostAsJsonAsync("/api/v1/media/uploads", initRequest);
        var sessionDto = await initRes.Content.ReadFromJsonAsync<UploadSessionDto>();
        Assert.NotNull(sessionDto);

        // Tải binary giả mạo (không phải JPEG magic bytes)
        byte[] fakeBytes = System.Text.Encoding.UTF8.GetBytes("THIS IS PLAIN TEXT NOT A JPEG IMAGE!!");
        using var minioClient = new HttpClient();
        using var content = new ByteArrayContent(fakeBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var putRes = await minioClient.PutAsync(sessionDto.UploadUrl, content);
        Assert.True(putRes.IsSuccessStatusCode);

        // Gọi Complete
        var completeRes = await client.PostAsync($"/api/v1/media/uploads/{sessionDto.UploadId}/complete", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completeRes.StatusCode);

        // Kiểm tra database: MediaAsset và Session chuyển sang Rejected
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaDbContext>();
            var session = await db.MediaUploadSessions.Include(s => s.MediaAsset).FirstOrDefaultAsync(s => s.Id == sessionDto.UploadId);
            Assert.NotNull(session);
            Assert.Equal(UploadSessionStatus.Rejected, session.Status);
            Assert.Equal(MediaStatus.Rejected, session.MediaAsset!.Status);

            var cleanupItem = await db.MediaCleanupItems.FirstOrDefaultAsync(c => c.StorageKey == session.TemporaryStorageKey);
            Assert.NotNull(cleanupItem);
            Assert.Equal(CleanupItemStatus.Pending, cleanupItem.Status);
        }

        // Chạy service dọn dẹp để xóa object rác khỏi MinIO
        using (var scope = factory.Services.CreateScope())
        {
            var cleanupService = scope.ServiceProvider.GetRequiredService<IMediaCleanupService>();
            int processed = await cleanupService.ProcessPendingCleanupsAsync();
            Assert.True(processed >= 1);
        }
    }

    [Fact]
    public async Task CompleteUpload_WhenCalledConcurrently_ShouldSucceedWithoutConflict()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();

        string userId = $"user-{Guid.NewGuid():N}";
        string idempotencyKey = Guid.NewGuid().ToString("N");
        byte[] jpegBytes = CreateValidJpegBytes(1024);

        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var initRequest = new InitializeUploadRequest(
            Purpose: "equipment-image",
            OwnerEntityId: "equipment-test-concurrent",
            FileName: "concurrent.jpg",
            ContentType: "image/jpeg",
            SizeBytes: jpegBytes.Length);

        var initRes = await client.PostAsJsonAsync("/api/v1/media/uploads", initRequest);
        var sessionDto = await initRes.Content.ReadFromJsonAsync<UploadSessionDto>();
        Assert.NotNull(sessionDto);

        // Upload file lên MinIO
        using var minioClient = new HttpClient();
        using var content = new ByteArrayContent(jpegBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        var putRes = await minioClient.PutAsync(sessionDto.UploadUrl, content);
        Assert.True(putRes.IsSuccessStatusCode);

        // Gửi 2 request Complete đồng thời
        using var client1 = factory.CreateClient();
        client1.DefaultRequestHeaders.Add("X-User-Id", userId);

        using var client2 = factory.CreateClient();
        client2.DefaultRequestHeaders.Add("X-User-Id", userId);

        var task1 = client1.PostAsync($"/api/v1/media/uploads/{sessionDto.UploadId}/complete", null);
        var task2 = client2.PostAsync($"/api/v1/media/uploads/{sessionDto.UploadId}/complete", null);

        var responses = await Task.WhenAll(task1, task2);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}
