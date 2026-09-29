using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Clients;

/// <summary>Cổng giao tiếp nội bộ tới MediaService.</summary>
public interface IMediaServiceClient
{
    /// <summary>Xác minh file MediaId đang Ready, đúng loại và người upload trước khi gắn vào hồ sơ/tổ chức.</summary>
    Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>Yêu cầu URL ký ngắn hạn để đọc file riêng tư (giấy phép kinh doanh).</summary>
    Task<Result<MediaReadUrlDto>> CreatePrivateAccessUrlAsync(Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>Thiết lập trạng thái hiển thị của file (public/private).</summary>
    Task<Result<MediaVisibilityDto>> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default);
}
