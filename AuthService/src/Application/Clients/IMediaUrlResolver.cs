using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Common;

namespace Dprental.AuthService.Application.Clients;

/// <summary>Dịch vụ chuyển đổi MediaId thành URL hiển thị (public ổn định hoặc private ký ngắn hạn).</summary>
public interface IMediaUrlResolver
{
    /// <summary>Sinh URL public ổn định qua Gateway cho avatar và logo: /api/v1/media/{mediaId}/content.</summary>
    string? ResolvePublicUrl(Guid? mediaId);

    /// <summary>Lấy URL ký ngắn hạn kèm hạn dùng cho file private (giấy phép kinh doanh) qua MediaService POST /access.</summary>
    Task<Result<MediaReadUrlDto>> ResolvePrivateAccessUrlAsync(Guid mediaId, CancellationToken cancellationToken = default);
}
