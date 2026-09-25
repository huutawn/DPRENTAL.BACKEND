namespace Dprental.MediaService.Common.Enums;

/// <summary>
/// Trạng thái vòng đời của một MediaAsset.
/// </summary>
public enum MediaStatus
{
    /// <summary>
    /// Đang chờ hoặc đang trong tiến trình tải lên từ client.
    /// </summary>
    PendingUpload = 0,

    /// <summary>
    /// Đã tải lên và xác minh thành công, sẵn sàng liên kết với entity nghiệp vụ.
    /// </summary>
    Ready = 1,

    /// <summary>
    /// Bị từ chối do không đáp ứng policy (định dạng, kích thước, magic bytes, checksum...).
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// Đã xóa logic.
    /// </summary>
    Deleted = 3
}

/// <summary>
/// Trạng thái hiển thị công khai hoặc riêng tư của Media.
/// </summary>
public enum MediaVisibility
{
    /// <summary>
    /// Riêng tư, chỉ truy cập qua URL ký sẵn khi có quyền.
    /// </summary>
    Private = 0,

    /// <summary>
    /// Công khai, có thể xem trực tiếp qua URL công khai.
    /// </summary>
    Public = 1
}

/// <summary>
/// Trạng thái của một phiên tải lên MediaUploadSession.
/// </summary>
public enum UploadSessionStatus
{
    /// <summary>
    /// Phiên đang mở, chờ client PUT binary lên MinIO và gọi Complete.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Đã hoàn tất xác minh file và chuyển sang vị trí lưu trữ chính thức.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// Phiên đã quá hạn 15 phút, không thể tiếp tục tải lên hay hoàn tất.
    /// </summary>
    Expired = 2,

    /// <summary>
    /// Tệp tải lên bị từ chối trong bước Complete.
    /// </summary>
    Rejected = 3
}

/// <summary>
/// Trạng thái xử lý của một tác vụ dọn dẹp object rác trong Object Storage.
/// </summary>
public enum CleanupItemStatus
{
    /// <summary>
    /// Đang chờ xử lý dọn dẹp.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Đang trong quá trình xóa object trên storage.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Đã dọn dẹp thành công.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Dọn dẹp thất bại sau số lần retry tối đa.
    /// </summary>
    Failed = 3
}
