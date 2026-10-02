namespace Dprental.CatalogService.Application.Clients;

/// <summary>Kết quả xác minh một MediaId với Media Service.</summary>
public enum MediaVerificationStatus
{
    /// <summary>Media tồn tại và đang ở trạng thái Ready.</summary>
    Ready,

    /// <summary>Media không tồn tại bên Media Service.</summary>
    NotFound,

    /// <summary>Media tồn tại nhưng chưa Ready (đang upload, bị từ chối hoặc đã xóa).</summary>
    NotReady,

    /// <summary>Không xác minh được: Media Service lỗi, timeout hoặc từ chối xác thực.</summary>
    Unavailable
}
