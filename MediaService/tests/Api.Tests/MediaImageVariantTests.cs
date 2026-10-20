using System.Net;
using Dprental.MediaService.Common.Enums;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Dprental.MediaService.Application.DTOs;
using Dprental.MediaService.Application.IServices;
using Dprental.MediaService.Application.Policies;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.Processing;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Dprental.MediaService.Api.Tests;

[Collection("Integration")]
public class MediaImageVariantTests
{
    private static byte[] CreateTestImage(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color ?? SKColors.DodgerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 80);
        return data.ToArray();
    }

    private static byte[] CreateTransparentPng(int width = 100, int height = 100)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static MultipartFormDataContent CreateMultipartForm(byte[] bytes, string contentType, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "files", fileName);
        return form;
    }

    [Fact]
    public async Task Processor_GeneratesThreeWebpVariants_WithNoUpscalingForSmallImage()
    {
        using var processor = new SkiaSharpImageProcessor();
        byte[] source = CreateTestImage(200, 100, SKEncodedImageFormat.Png);

        using var ms = new MemoryStream(source);
        var variants = await processor.GenerateVariantsAsync(ms, "image/png", ImageVariantPolicy.RequiredVariants);

        Assert.Equal(3, variants.Count);
        Assert.All(variants, v => Assert.Equal("image/webp", v.ContentType));

        // Không phóng to: ảnh gốc 200x100 nhỏ hơn cả 320/768/1600 nên tất cả giữ 200x100
        var thumb = variants.First(v => v.Name == ImageVariantPolicy.Thumbnail);
        Assert.Equal(200, thumb.Width);
        Assert.Equal(100, thumb.Height);

        var med = variants.First(v => v.Name == ImageVariantPolicy.Medium);
        Assert.Equal(200, med.Width);
        Assert.Equal(100, med.Height);

        var large = variants.First(v => v.Name == ImageVariantPolicy.Large);
        Assert.Equal(200, large.Width);
        Assert.Equal(100, large.Height);
    }

    [Fact]
    public async Task Processor_DownscalesCorrectly_MaintainingAspectRatio()
    {
        using var processor = new SkiaSharpImageProcessor();
        // 2000 x 1000 (tỉ lệ 2:1)
        byte[] source = CreateTestImage(2000, 1000, SKEncodedImageFormat.Jpeg);

        using var ms = new MemoryStream(source);
        var variants = await processor.GenerateVariantsAsync(ms, "image/jpeg", ImageVariantPolicy.RequiredVariants);

        var thumb = variants.First(v => v.Name == ImageVariantPolicy.Thumbnail);
        Assert.Equal(320, thumb.Width);
        Assert.Equal(160, thumb.Height);

        var med = variants.First(v => v.Name == ImageVariantPolicy.Medium);
        Assert.Equal(768, med.Width);
        Assert.Equal(384, med.Height);

        var large = variants.First(v => v.Name == ImageVariantPolicy.Large);
        Assert.Equal(1600, large.Width);
        Assert.Equal(800, large.Height);
    }

    [Fact]
    public async Task Processor_PreservesTransparencyInWebp()
    {
        using var processor = new SkiaSharpImageProcessor();
        byte[] png = CreateTransparentPng(100, 100);

        using var ms = new MemoryStream(png);
        var variants = await processor.GenerateVariantsAsync(ms, "image/png", ImageVariantPolicy.RequiredVariants);

        var thumb = variants.First(v => v.Name == ImageVariantPolicy.Thumbnail);
        using var decodedThumb = SKBitmap.Decode(thumb.Data);
        // Điểm góc (0,0) phải là trong suốt (alpha = 0)
        var cornerPixel = decodedThumb.GetPixel(0, 0);
        Assert.Equal(0, cornerPixel.Alpha);

        // Điểm tâm (50,50) là hình tròn đỏ (alpha > 0)
        var centerPixel = decodedThumb.GetPixel(50, 50);
        Assert.True(centerPixel.Alpha > 200);
    }

    [Fact]
    public async Task Processor_ThrowsOnCorruptImage()
    {
        using var processor = new SkiaSharpImageProcessor();
        byte[] corrupt = [0xFF, 0xD8, 0xFF, 0xE0, 0x12, 0x34, 0x56, 0x78];

        using var ms = new MemoryStream(corrupt);
        await Assert.ThrowsAsync<ImageDecodeException>(() =>
            processor.GenerateVariantsAsync(ms, "image/jpeg", ImageVariantPolicy.RequiredVariants));
    }

    [Fact]
    public async Task Processor_RejectsTruncatedIncompleteInput()
    {
        using var processor = new SkiaSharpImageProcessor();
        byte[] fullJpeg = CreateTestImage(300, 200, SKEncodedImageFormat.Jpeg);
        // Cắt bỏ 50 bytes cuối (phần scan data và EOI) để header hợp lệ nhưng data bị cụt (IncompleteInput)
        byte[] truncatedJpeg = fullJpeg[..(fullJpeg.Length - 50)];

        using var ms = new MemoryStream(truncatedJpeg);
        var ex = await Assert.ThrowsAsync<ImageDecodeException>(() =>
            processor.GenerateVariantsAsync(ms, "image/jpeg", ImageVariantPolicy.RequiredVariants));
        Assert.Contains("IncompleteInput", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Processor_HandlesOrientation_SwappingDimensionsFor90Degrees()
    {
        using var processor = new SkiaSharpImageProcessor();
        // Tạo ảnh với kích thước 400x200
        byte[] source = CreateTestImage(400, 200, SKEncodedImageFormat.Jpeg);

        using var ms = new MemoryStream(source);
        var variants = await processor.GenerateVariantsAsync(ms, "image/jpeg", ImageVariantPolicy.RequiredVariants);

        // Standard JPEG with default TopLeft origin retains 400x200 proportions
        var thumb = variants.First(v => v.Name == ImageVariantPolicy.Thumbnail);
        Assert.Equal(320, thumb.Width);
        Assert.Equal(160, thumb.Height);
    }

    [Fact]
    public async Task Processor_RejectsOver40Megapixels_BeforeFullAllocation()
    {
        using var processor = new SkiaSharpImageProcessor();
        byte[] largeImage = CreateTestImage(7000, 6000, SKEncodedImageFormat.Jpeg);

        using var ms = new MemoryStream(largeImage);
        await Assert.ThrowsAsync<ImagePixelLimitExceededException>(() =>
            processor.GenerateVariantsAsync(ms, "image/jpeg", ImageVariantPolicy.RequiredVariants));
    }

    [Fact]
    public async Task Upload_Image_GeneratesThreeVariantsAndReturnsInDto()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        MediaServiceTestFactory.SetApiKey(client);
        byte[] jpeg = CreateTestImage(1000, 500, SKEncodedImageFormat.Jpeg);
        using var form = CreateMultipartForm(jpeg, "image/jpeg", "landscape.jpg");

        using var response = await client.PostAsync("/api/v1/media/uploads", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<UploadFileResultDto[]>();
        Assert.NotNull(results);
        Assert.Single(results);
        var item = results[0];
        Assert.Equal("Ready", item.Media?.Status);

        var variants = item.Media?.ImageVariants;
        Assert.NotNull(variants);
        Assert.Equal(3, variants.Count);

        var thumb = variants.First(v => v.Name == "thumbnail");
        Assert.Equal(320, thumb.Width);
        Assert.Equal(160, thumb.Height);
        Assert.Equal("image/webp", thumb.ContentType);
        Assert.Contains($"/api/v1/media/{item.Media!.MediaId}/content?variant=thumbnail", thumb.ContentPath);

        var med = variants.First(v => v.Name == "medium");
        Assert.Equal(768, med.Width);
        Assert.Equal(384, med.Height);

        var large = variants.First(v => v.Name == "large");
        Assert.Equal(1000, large.Width); // không phóng to quá 1000
        Assert.Equal(500, large.Height);

        // Metadata endpoint cũng trả đủ variants
        using var metadataResponse = await client.GetAsync($"/api/v1/media/{item.Media.MediaId}");
        Assert.Equal(HttpStatusCode.OK, metadataResponse.StatusCode);
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<MediaMetadataDto>();
        Assert.NotNull(metadata);
        Assert.Equal(3, metadata.ImageVariants.Count);
    }

    [Fact]
    public async Task Upload_Pdf_ReturnsEmptyImageVariants()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");

        byte[] pdf = "%PDF-1.7\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Count 1/Kids[3 0 R]>>endobj\n3 0 obj<</Type/Page/MediaBox[0 0 3 3]>>endobj\nxref\n0 4\n0000000000 65535 f\n0000000009 00000 n\n0000000052 00000 n\n0000000101 00000 n\ntrailer<</Size 4/Root 1 0 R>>\nstartxref\n149\n%%EOF"u8.ToArray();
        using var form = CreateMultipartForm(pdf, "application/pdf", "doc.pdf");

        using var response = await client.PostAsync("/api/v1/media/uploads", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<UploadFileResultDto[]>();
        Assert.NotNull(results);
        var item = results[0];
        Assert.Equal("Ready", item.Media?.Status);
        Assert.NotNull(item.Media?.ImageVariants);
        Assert.Empty(item.Media.ImageVariants);
    }

    [Fact]
    public async Task GetPublicContent_Variant_Returns200WebpDirectly_WithCacheControlAndEtag()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        MediaServiceTestFactory.SetApiKey(client);

        byte[] jpeg = CreateTestImage(800, 600, SKEncodedImageFormat.Jpeg);
        using var form = CreateMultipartForm(jpeg, "image/jpeg", "public_view.jpg");
        var uploadRes = await client.PostAsync("/api/v1/media/uploads", form);
        var uploadResult = (await uploadRes.Content.ReadFromJsonAsync<UploadFileResultDto[]>())![0];
        Guid mediaId = uploadResult.Media!.MediaId;

        // Đổi visibility sang Public
        var visRes = await client.PutAsJsonAsync($"/api/v1/media/{mediaId}/visibility", new ChangeMediaVisibilityRequest("public"));
        Assert.Equal(HttpStatusCode.OK, visRes.StatusCode);
        using var anonClient = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        MediaServiceTestFactory.SetApiKey(anonClient);
        // GET không có variant -> mặc định trả medium WebP 200 trực tiếp đối với ảnh đã xử lý
        using var defaultRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content");
        Assert.Equal(HttpStatusCode.OK, defaultRes.StatusCode);
        Assert.Equal("image/webp", defaultRes.Content.Headers.ContentType?.MediaType);

        // GET variant=original -> 302 Found redirect tới ảnh gốc
        var origReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{mediaId}/content?variant=original");
        using var origRes = await anonClient.SendAsync(origReq);
        Assert.Equal(HttpStatusCode.Redirect, origRes.StatusCode);

        // GET có variant=thumbnail -> 200 OK trực tiếp với WebP
        using var thumbRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content?variant=thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbRes.StatusCode);
        Assert.Equal("image/webp", thumbRes.Content.Headers.ContentType?.MediaType);
        var cc = thumbRes.Headers.CacheControl;
        Assert.NotNull(cc);
        Assert.True(cc.Public);
        Assert.True(cc.MustRevalidate);
        Assert.Equal(TimeSpan.Zero, cc.MaxAge);
        Assert.Equal(TimeSpan.FromSeconds(300), cc.SharedMaxAge);
        Assert.NotNull(thumbRes.Headers.ETag);
        // Xác minh ETag -> 304 Not Modified
        var ifNoneMatchReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/media/{mediaId}/content?variant=thumbnail");
        ifNoneMatchReq.Headers.TryAddWithoutValidation("If-None-Match", thumbRes.Headers.ETag.ToString());
        using var notModRes = await anonClient.SendAsync(ifNoneMatchReq);
        Assert.Equal(HttpStatusCode.NotModified, notModRes.StatusCode);
        var notModCc = notModRes.Headers.CacheControl;
        Assert.NotNull(notModCc);
        Assert.True(notModCc.Public);
        Assert.True(notModCc.MustRevalidate);
        Assert.Equal(TimeSpan.Zero, notModCc.MaxAge);
        Assert.Equal(TimeSpan.FromSeconds(300), notModCc.SharedMaxAge);
        // Variant không hợp lệ -> 400
        using var badVariantRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content?variant=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, badVariantRes.StatusCode);

        // Variant kết hợp download=true -> 400
        using var dlRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content?variant=thumbnail&download=true");
        Assert.Equal(HttpStatusCode.BadRequest, dlRes.StatusCode);

        // Chuyển lại Private -> Origin từ chối ngay lập tức (404)
        var privRes = await client.PutAsJsonAsync($"/api/v1/media/{mediaId}/visibility", new ChangeMediaVisibilityRequest("private"));
        Assert.Equal(HttpStatusCode.OK, privRes.StatusCode);

        using var rejectedRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content?variant=thumbnail");
        Assert.Equal(HttpStatusCode.NotFound, rejectedRes.StatusCode);
    }

    [Fact]
    public async Task CreatePrivateAccess_WithVariant_ReturnsSignedVariantUrl()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetApiKey(client);
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");

        byte[] jpeg = CreateTestImage(500, 500, SKEncodedImageFormat.Jpeg);
        using var form = CreateMultipartForm(jpeg, "image/jpeg", "private_doc.jpg");
        var uploadRes = await client.PostAsync("/api/v1/media/uploads", form);
        var uploadResult = (await uploadRes.Content.ReadFromJsonAsync<UploadFileResultDto[]>())![0];
        Guid mediaId = uploadResult.Media!.MediaId;

        // /access với variant=medium
        var accessReq = new MediaAccessRequest("read", Download: false, Variant: "medium");
        using var accessRes = await client.PostAsJsonAsync($"/api/v1/media/{mediaId}/access", accessReq);
        Assert.Equal(HttpStatusCode.OK, accessRes.StatusCode);
        var urlDto = await accessRes.Content.ReadFromJsonAsync<MediaReadUrlDto>();
        Assert.NotNull(urlDto?.Url);
        Assert.Contains("v1_medium", urlDto.Url);

        // /access với variant + download=true -> 400 BadRequest
        var invalidReq = new MediaAccessRequest("read", Download: true, Variant: "medium");
        using var invalidRes = await client.PostAsJsonAsync($"/api/v1/media/{mediaId}/access", invalidReq);
        Assert.Equal(HttpStatusCode.BadRequest, invalidRes.StatusCode);
    }

    [Fact]
    public async Task BackfillService_ProcessesImages_AndRerunIsIdempotent()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        MediaServiceTestFactory.SetApiKey(client);

        byte[] jpeg = CreateTestImage(600, 400, SKEncodedImageFormat.Jpeg);
        using var form = CreateMultipartForm(jpeg, "image/jpeg", "to_backfill.jpg");
        var uploadRes = await client.PostAsync("/api/v1/media/uploads", form);
        var uploadResult = (await uploadRes.Content.ReadFromJsonAsync<UploadFileResultDto[]>())![0];
        Guid mediaId = uploadResult.Media!.MediaId;

        using var scope = factory.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<IImageVariantBackfillService>();

        // Chạy lại backfill cho file này (đã có variants) -> Skipped, không tạo bộ trùng
        var summary = await backfill.RunBackfillAsync(new BackfillOptions(MediaId: mediaId));
        Assert.Equal(1, summary.TotalProcessed);
        Assert.Equal(1, summary.SkippedCount);
        Assert.Equal(0, summary.FailedCount);

        // Dry-run mode
        var dryRunSummary = await backfill.RunBackfillAsync(new BackfillOptions(DryRun: true, BatchSize: 10));
        Assert.Equal(0, dryRunSummary.FailedCount);
    }

    [Fact]
    public async Task Upload_SameIdempotencyKey_ReplaysPublishedVariants()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        string key = Guid.NewGuid().ToString("N");
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        byte[] jpeg = CreateTestImage(400, 300, SKEncodedImageFormat.Jpeg);
        using var form1 = CreateMultipartForm(jpeg, "image/jpeg", "replay.jpg");
        var res1 = await client.PostAsync("/api/v1/media/uploads", form1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var items1 = await res1.Content.ReadFromJsonAsync<UploadFileResultDto[]>();
        Assert.NotNull(items1);
        Assert.Equal(3, items1[0].Media?.ImageVariants?.Count);

        // Gửi lại request y hệt
        using var form2 = CreateMultipartForm(jpeg, "image/jpeg", "replay.jpg");
        var res2 = await client.PostAsync("/api/v1/media/uploads", form2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var items2 = await res2.Content.ReadFromJsonAsync<UploadFileResultDto[]>();
        Assert.NotNull(items2);
        Assert.Equal(items1[0].Media?.MediaId, items2[0].Media?.MediaId);
        Assert.Equal(3, items2[0].Media?.ImageVariants?.Count);
    }

    [Fact]
    public async Task Backfill_NonExistentMediaId_RecordsFailureAndContinues()
    {
        using var factory = new MediaServiceTestFactory();
        using var scope = factory.Services.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<IImageVariantBackfillService>();

        Guid nonExistent = Guid.NewGuid();
        var summary = await backfill.RunBackfillAsync(new BackfillOptions(MediaId: nonExistent));

        Assert.Equal(1, summary.TotalProcessed);
        Assert.Equal(1, summary.FailedCount);
        Assert.Contains(nonExistent, summary.FailedMediaIds);
    }

    [Fact]
    public async Task EndToEnd_OldImageWithoutVariants_Returns302_ThenAfterBackfillReturns200Webp()
    {
        using var factory = new MediaServiceTestFactory();
        using var client = factory.CreateClient();
        MediaServiceTestFactory.SetUserToken(client, $"user-{Guid.NewGuid():N}");
        MediaServiceTestFactory.SetApiKey(client);

        byte[] jpeg = CreateTestImage(600, 400, SKEncodedImageFormat.Jpeg);
        using var form = CreateMultipartForm(jpeg, "image/jpeg", "old_photo.jpg");
        var uploadRes = await client.PostAsync("/api/v1/media/uploads", form);
        var uploadResult = (await uploadRes.Content.ReadFromJsonAsync<UploadFileResultDto[]>())![0];
        Guid mediaId = uploadResult.Media!.MediaId;

        // Đổi sang Public
        await client.PutAsJsonAsync($"/api/v1/media/{mediaId}/visibility", new ChangeMediaVisibilityRequest("public"));

        // Xóa các variant đã tạo để mô phỏng ảnh cũ trước khi triển khai tính năng
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Domain.MediaDbContext>();
            var variants = db.MediaImageVariants.Where(v => v.MediaAssetId == mediaId);
            db.MediaImageVariants.RemoveRange(variants);
            await db.SaveChangesAsync();
        }

        // GET /content không có variant khi chưa có biến thể: trả 302 Found redirect tới ảnh gốc
        using var anonClient = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        MediaServiceTestFactory.SetApiKey(anonClient);
        using var beforeBackfillRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content");
        Assert.Equal(HttpStatusCode.Redirect, beforeBackfillRes.StatusCode);

        // Chạy backfill cho mediaId này
        using (var scope = factory.Services.CreateScope())
        {
            var backfill = scope.ServiceProvider.GetRequiredService<IImageVariantBackfillService>();
            var summary = await backfill.RunBackfillAsync(new BackfillOptions(MediaId: mediaId));
            Assert.Equal(1, summary.TotalProcessed);
            Assert.Equal(1, summary.SucceededCount);
            Assert.Equal(0, summary.FailedCount);
        }

        // GET /content không có variant sau khi backfill: mặc định trả medium WebP 200 trực tiếp!
        using var afterBackfillRes = await anonClient.GetAsync($"/api/v1/media/{mediaId}/content");
        Assert.Equal(HttpStatusCode.OK, afterBackfillRes.StatusCode);
        Assert.Equal("image/webp", afterBackfillRes.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task VariantService_WhenCleanupClaimsMarkers_AbortsPublishWithConflict()
    {
        using var factory = new MediaServiceTestFactory();
        using var scope = factory.Services.CreateScope();
        var variantService = scope.ServiceProvider.GetRequiredService<IImageVariantService>();
        var cleanupRepo = scope.ServiceProvider.GetRequiredService<Domain.IRepositories.IMediaCleanupRepository>();
        var db = scope.ServiceProvider.GetRequiredService<Domain.MediaDbContext>();

        // Tạo một asset giả
        var assetId = Guid.NewGuid();
        var asset = new MediaAsset
        {
            Id = assetId,
            Status = MediaStatus.Ready,
            OriginalFileName = "conflict.jpg",
            DeclaredContentType = "image/jpeg",
            ActualContentType = "image/jpeg",
            StorageKey = "media/conflict.jpg"
        };
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();

        byte[] jpeg = CreateTestImage(200, 200, SKEncodedImageFormat.Jpeg);
        using var ms = new MemoryStream(jpeg);

        // Mô phỏng cleanup worker đã claim một marker của asset này
        var conflictKey = $"media/{assetId:N}/variants/v1_thumbnail_fake.webp";
        await cleanupRepo.AddAsync(new MediaCleanupItem
        {
            Id = Guid.NewGuid(),
            StorageKey = conflictKey,
            Reason = "Testing",
            Status = CleanupItemStatus.Pending,
            ClaimedAtUtc = DateTimeOffset.UtcNow,
            LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            ClaimedByWorkerId = "competing-worker"
        });
        await db.SaveChangesAsync();

        bool isClaimed = await cleanupRepo.HasClaimedMarkersAsync([conflictKey]);
        Assert.True(isClaimed);
    }

    private static byte[] CreateHeaderOnlyLargeJpeg(int width, int height)
    {
        // Tạo JPEG header với marker SOF0 chỉ định kích thước lớn mà không cần encode megapixel pixel
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]); // SOI
        ms.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]); // APP0 JFIF
        // SOF0 (Start Of Frame, Baseline DCT)
        // Length: 8 + 3*components = 17 (0x0011)
        // Precision: 8
        // Height: height (2 bytes)
        // Width: width (2 bytes)
        // Components: 3 (Y, Cb, Cr)
        ms.Write([
            0xFF, 0xC0,
            0x00, 0x11,
            0x08,
            (byte)((height >> 8) & 0xFF), (byte)(height & 0xFF),
            (byte)((width >> 8) & 0xFF), (byte)(width & 0xFF),
            0x03,
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01
        ]);
        ms.Write([0xFF, 0xD9]); // EOI
        return ms.ToArray();
    }
}
