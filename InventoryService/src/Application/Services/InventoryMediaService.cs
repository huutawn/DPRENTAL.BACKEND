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
    /// Chuẩn bị media công khai: kiểm tra quyền, verify trạng thái Ready, đặt Public và trả URL tham chiếu.
    /// Nếu mediaId là null, trả về thành công với giá trị null (không gọi remote).
    /// </summary>
    public async Task<Result<MediaReferenceResponse?>> PreparePublicAsync(
        Guid? mediaId,
        IInventoryCallerContext caller,
        CancellationToken cancellationToken = default)
    {
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

        // 2. Nếu mediaId null: trả null, không gọi remote
        if (mediaId is null)
        {
            return Result.Success<MediaReferenceResponse?>(null);
        }

        // 3. Nếu mediaId là Guid rỗng: trả validation error
        if (mediaId.Value == Guid.Empty)
        {
            return Error.Validation("INVALID_MEDIA_ID", "Media ID không được rỗng.");
        }

        // 4 & 5. Gọi VerifyAsync và xác nhận ID trùng, status là Ready, người upload là GUID hợp lệ
        var verifyResult = await _mediaClient.VerifyAsync(mediaId.Value, cancellationToken);
        if (verifyResult.IsFailure)
        {
            return verifyResult.Error;
        }

        var info = verifyResult.Value;
        if (info.MediaId != mediaId.Value)
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

        // 6. So sánh người upload với JWT sub của caller. Không dùng owner client truyền vào, organization hoặc admin bypass
        if (uploaderGuid != caller.UserId.Value)
        {
            return Error.Forbidden("MEDIA_OWNERSHIP_MISMATCH", "Chỉ người upload media mới có quyền sử dụng media này.");
        }

        // 7 & 8. Gọi SetPublicAsync và xác nhận response 200, đúng media ID và visibility Public
        var publishResult = await _mediaClient.SetPublicAsync(mediaId.Value, cancellationToken);
        if (publishResult.IsFailure)
        {
            return publishResult.Error;
        }

        var pub = publishResult.Value;
        if (pub.MediaId != mediaId.Value || !string.Equals(pub.Visibility, "Public", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unavailable("MEDIA_PUBLISH_FAILED", "Không thể công bố media công khai.");
        }

        // 9. Dựng URL ổn định từ PublicGatewayUrl và trả { id, url }
        var url = MediaContentUrlBuilder.Build(_options.PublicGatewayUrl, mediaId.Value)!;

        return Result.Success<MediaReferenceResponse?>(new MediaReferenceResponse(mediaId.Value, url));
    }
}
