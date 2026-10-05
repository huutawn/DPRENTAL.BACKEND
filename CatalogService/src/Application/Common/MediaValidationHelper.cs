using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Common;

namespace Dprental.CatalogService.Application.Common;

/// <summary>
/// Các quy tắc kiểm tra định dạng và trạng thái media được chấp nhận trong Catalog.
/// </summary>
public static class MediaValidationHelper
{
    public static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    public static bool IsAllowedImageType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) && AllowedImageMimeTypes.Contains(contentType.Trim());

    public static bool IsReady(string? status) =>
        string.Equals(status?.Trim(), "Ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Xác minh media: phải tồn tại, Ready, thuộc MIME ảnh được hỗ trợ, và khớp operator uploader (nếu có).
    /// Sau khi toàn bộ hợp lệ, đặt visibility sang "public".
    /// </summary>
    public static async Task<Result> ValidateAndPublishMediaAsync(
        IMediaServiceClient mediaServiceClient,
        Guid mediaId,
        string? operatorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaServiceClient);

        var verifyResult = await mediaServiceClient.VerifyMediaAsync(mediaId, cancellationToken);
        if (verifyResult.IsFailure)
        {
            return Result.Failure(verifyResult.Error);
        }

        var media = verifyResult.Value;
        if (!IsReady(media.Status))
        {
            return Result.Failure(CatalogErrors.Media.NotReady);
        }

        if (!IsAllowedImageType(media.ContentType))
        {
            return Result.Failure(CatalogErrors.Media.InvalidContentType);
        }

        if (!string.IsNullOrWhiteSpace(operatorUserId) &&
            !string.Equals(media.UploadedByUserId?.Trim(), operatorUserId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(CatalogErrors.Media.Forbidden);
        }

        var publicResult = await mediaServiceClient.SetVisibilityAsync(mediaId, "public", cancellationToken);
        if (publicResult.IsFailure)
        {
            return Result.Failure(publicResult.Error);
        }

        return Result.Success();
    }

    /// <summary>
    /// Xác minh danh sách media: toàn bộ phải hợp lệ trước khi đổi visibility sang "public".
    /// </summary>
    public static async Task<Result> ValidateAndPublishMediaListAsync(
        IMediaServiceClient mediaServiceClient,
        IEnumerable<Guid> mediaIds,
        string? operatorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaServiceClient);
        ArgumentNullException.ThrowIfNull(mediaIds);

        var distinctMediaIds = mediaIds.Distinct().ToList();
        if (distinctMediaIds.Count == 0)
        {
            return Result.Success();
        }

        foreach (var mediaId in distinctMediaIds)
        {
            var verifyResult = await mediaServiceClient.VerifyMediaAsync(mediaId, cancellationToken);
            if (verifyResult.IsFailure)
            {
                return Result.Failure(verifyResult.Error);
            }

            var media = verifyResult.Value;
            if (!IsReady(media.Status))
            {
                return Result.Failure(CatalogErrors.Media.NotReady);
            }

            if (!IsAllowedImageType(media.ContentType))
            {
                return Result.Failure(CatalogErrors.Media.InvalidContentType);
            }

            if (!string.IsNullOrWhiteSpace(operatorUserId) &&
                !string.Equals(media.UploadedByUserId?.Trim(), operatorUserId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(CatalogErrors.Media.Forbidden);
            }
        }

        foreach (var mediaId in distinctMediaIds)
        {
            var publicResult = await mediaServiceClient.SetVisibilityAsync(mediaId, "public", cancellationToken);
            if (publicResult.IsFailure)
            {
                return Result.Failure(publicResult.Error);
            }
        }

        return Result.Success();
    }
}
