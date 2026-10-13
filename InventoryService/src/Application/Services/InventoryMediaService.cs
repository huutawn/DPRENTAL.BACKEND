using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Mapping;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Application service điều phối xác minh, công bố Public và dựng URL ổn định cho Media theo hợp đồng tích hợp.
/// </summary>
public sealed class InventoryMediaService : IInventoryMediaService
{
    private readonly IMediaServiceClient _mediaClient;
    private readonly MediaServiceOptions _options;

    public InventoryMediaService(
        IMediaServiceClient mediaClient,
        IOptions<MediaServiceOptions> options)
    {
        _mediaClient = mediaClient ?? throw new ArgumentNullException(nameof(mediaClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Giá trị MediaServiceOptions không được null.", nameof(options));
    }

    /// <summary>
    /// Chuẩn bị danh sách media công khai và ảnh đại diện: kiểm tra quyền, cấu trúc danh sách,
    /// verify trạng thái Ready, quyền sở hữu, ContentType (media là ảnh/video, thumbnail là ảnh),
    /// đặt Public cho các media mới và trả danh sách tham chiếu giữ nguyên thứ tự.
    /// </summary>
    public async Task<Result<IReadOnlyList<MediaReferenceResponse>>> PreparePublicAsync(
        IReadOnlyList<Guid> mediaIds,
        Guid? thumbnailMediaId,
        IInventoryCallerContext caller,
        IReadOnlyCollection<Guid>? existingMediaIds = null,
        Guid? existingThumbnailMediaId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        ArgumentNullException.ThrowIfNull(caller);

        // 1. Kiểm tra caller là authenticated human và có UserId hợp lệ
        if (!caller.IsAuthenticated)
        {
            return Error.Unauthorized("UNAUTHORIZED", "Yêu cầu xác thực danh tính.");
        }

        if (!caller.IsHuman || caller.UserId is null || caller.UserId == Guid.Empty)
        {
            return Error.Forbidden("FORBIDDEN", "Chỉ người dùng cá nhân hợp lệ mới được thực hiện thao tác này.");
        }

        // 2. Kiểm tra giới hạn, GUID rỗng, trùng lặp và thumbnail thuộc danh sách
        if (mediaIds.Count > Domain.Entities.EquipmentListing.MaxMediaCount)
        {
            return Error.Validation("MEDIA_LIMIT_EXCEEDED", $"Danh sách media tối đa {Domain.Entities.EquipmentListing.MaxMediaCount} mục.");
        }

        var seen = new HashSet<Guid>();
        foreach (var mediaId in mediaIds)
        {
            if (mediaId == Guid.Empty)
            {
                return Error.Validation("INVALID_MEDIA_ID", "Media ID không được rỗng.");
            }

            if (!seen.Add(mediaId))
            {
                return Error.Validation("DUPLICATE_MEDIA_ID", "Danh sách media không được chứa ID trùng lặp.");
            }
        }

        if (thumbnailMediaId.HasValue)
        {
            if (thumbnailMediaId.Value == Guid.Empty)
            {
                return Error.Validation("INVALID_THUMBNAIL_MEDIA_ID", "Thumbnail Media ID không được rỗng.");
            }

            if (!seen.Contains(thumbnailMediaId.Value))
            {
                return Error.Validation("INVALID_THUMBNAIL_MEDIA_ID", "Ảnh đại diện (thumbnailMediaId) phải nằm trong danh sách media của Listing.");
            }
        }

        if (mediaIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<MediaReferenceResponse>>(Array.Empty<MediaReferenceResponse>());
        }

        var existingSet = existingMediaIds is { Count: > 0 } ? new HashSet<Guid>(existingMediaIds) : null;
        var newMediaIds = new List<Guid>(mediaIds.Count);
        foreach (var mediaId in mediaIds)
        {
            if (existingSet is null || !existingSet.Contains(mediaId))
            {
                newMediaIds.Add(mediaId);
            }
        }

        // 3. Xác minh toàn bộ media mới trước khi gọi SetPublicAsync
        foreach (var mediaId in newMediaIds)
        {
            var verifyResult = await _mediaClient.VerifyAsync(mediaId, cancellationToken);
            if (verifyResult.IsFailure)
            {
                return verifyResult.Error;
            }

            var info = verifyResult.Value;
            if (info.MediaId != mediaId)
            {
                return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "ID media phản hồi không khớp.");
            }

            if (!string.Equals(info.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("MEDIA_NOT_READY", $"Media chưa sẵn sàng để sử dụng (trạng thái: {info.Status}).");
            }

            if (!Guid.TryParse(info.UploadedByUserId, out var uploaderGuid) || uploaderGuid == Guid.Empty)
            {
                return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Định danh người upload media không hợp lệ.");
            }

            // So sánh người upload với JWT sub của caller. Không dùng owner client truyền vào, organization hoặc admin bypass
            if (uploaderGuid != caller.UserId.Value)
            {
                return Error.Forbidden("MEDIA_OWNERSHIP_MISMATCH", "Chỉ người upload media mới có quyền sử dụng media này.");
            }

            if (string.IsNullOrWhiteSpace(info.ContentType))
            {
                return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Phản hồi verify từ MediaService thiếu ContentType.");
            }

            if (!IsImageOrVideoContentType(info.ContentType))
            {
                return Error.Validation("INVALID_MEDIA_CONTENT_TYPE", $"Định dạng media '{info.ContentType}' không được hỗ trợ. Chỉ chấp nhận ảnh hoặc video.");
            }

            if (thumbnailMediaId.HasValue && thumbnailMediaId.Value == mediaId && !IsImageContentType(info.ContentType))
            {
                return Error.Validation("INVALID_THUMBNAIL_CONTENT_TYPE", "Ảnh đại diện (thumbnail) bắt buộc phải là định dạng hình ảnh.");
            }
        }

        // 4. Nếu chọn lại thumbnail từ media đã có sẵn trong listing, xác minh thumbnail đó là ảnh và ở trạng thái Ready
        if (thumbnailMediaId.HasValue &&
            thumbnailMediaId != existingThumbnailMediaId &&
            existingSet is not null &&
            existingSet.Contains(thumbnailMediaId.Value))
        {
            var thumbVerify = await _mediaClient.VerifyAsync(thumbnailMediaId.Value, cancellationToken);
            if (thumbVerify.IsFailure)
            {
                return thumbVerify.Error;
            }

            var thumbInfo = thumbVerify.Value;
            if (thumbInfo.MediaId != thumbnailMediaId.Value)
            {
                return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "ID media phản hồi không khớp.");
            }

            if (!string.Equals(thumbInfo.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Conflict("MEDIA_NOT_READY", $"Media chưa sẵn sàng để sử dụng (trạng thái: {thumbInfo.Status}).");
            }

            if (string.IsNullOrWhiteSpace(thumbInfo.ContentType))
            {
                return Error.Unavailable("MEDIA_SERVICE_INVALID_RESPONSE", "Phản hồi verify từ MediaService thiếu ContentType.");
            }

            if (!IsImageContentType(thumbInfo.ContentType))
            {
                return Error.Validation("INVALID_THUMBNAIL_CONTENT_TYPE", "Ảnh đại diện (thumbnail) bắt buộc phải là định dạng hình ảnh.");
            }
        }

        // 5. Đặt Public cho các media mới
        foreach (var mediaId in newMediaIds)
        {
            var publishResult = await _mediaClient.SetPublicAsync(mediaId, cancellationToken);
            if (publishResult.IsFailure)
            {
                return publishResult.Error;
            }

            var pub = publishResult.Value;
            if (pub.MediaId != mediaId || !string.Equals(pub.Visibility, "Public", StringComparison.OrdinalIgnoreCase))
            {
                return Error.Unavailable("MEDIA_PUBLISH_FAILED", "Không thể công bố media công khai.");
            }
        }

        // 6. Dựng danh sách tham chiếu URL ổn định theo đúng thứ tự mediaIds
        return Result.Success(MediaContentUrlBuilder.BuildList(_options.PublicGatewayUrl, mediaIds));
    }

    public static bool IsImageContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = ExtractMediaType(contentType);
        return mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
               mediaType.Length > "image/".Length;
    }

    public static bool IsVideoContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = ExtractMediaType(contentType);
        return mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) &&
               mediaType.Length > "video/".Length;
    }

    public static bool IsImageOrVideoContentType(string? contentType) =>
        IsImageContentType(contentType) || IsVideoContentType(contentType);

    private static string ExtractMediaType(string contentType)
    {
        int semicolonIndex = contentType.IndexOf(';');
        var raw = semicolonIndex >= 0 ? contentType[..semicolonIndex] : contentType;
        return raw.Trim();
    }
}
