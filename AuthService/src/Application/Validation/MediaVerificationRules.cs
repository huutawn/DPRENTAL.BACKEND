using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Validation;

/// <summary>Quy tắc kiểm tra tính hợp lệ của file Media trước khi liên kết với hồ sơ người dùng hoặc tổ chức.</summary>
public static class MediaVerificationRules
{
    private static readonly HashSet<string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private static readonly HashSet<string> DocumentMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    public static Result ValidateImage(MediaVerificationDto dto, Guid expectedUserId)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!string.Equals(dto.Status, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.Media.NotReady);
        }

        if (!string.Equals(dto.UploadedByUserId, expectedUserId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.Media.Forbidden);
        }

        if (!ImageMimeTypes.Contains(dto.ContentType))
        {
            return Result.Failure(AuthErrors.Media.InvalidType);
        }

        return Result.Success();
    }

    public static Result ValidateDocument(MediaVerificationDto dto, Guid expectedUserId)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!string.Equals(dto.Status, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.Media.NotReady);
        }

        if (!string.Equals(dto.UploadedByUserId, expectedUserId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(AuthErrors.Media.Forbidden);
        }

        if (!DocumentMimeTypes.Contains(dto.ContentType))
        {
            return Result.Failure(AuthErrors.Media.InvalidType);
        }

        return Result.Success();
    }
}
