namespace Dprental.ReviewService.Common.Enums;

/// <summary>
/// Hành động kiểm toán trên bản ghi đánh giá.
/// </summary>
public enum ReviewAuditAction
{
    /// <summary>Đánh giá được tạo mới.</summary>
    Created = 1,

    /// <summary>Đánh giá được chỉnh sửa.</summary>
    Updated = 2,

    /// <summary>Đánh giá bị tác giả rút.</summary>
    Withdrawn = 3,

    /// <summary>Đánh giá bị quản trị viên ẩn.</summary>
    Hidden = 4,

    /// <summary>Đánh giá được quản trị viên khôi phục.</summary>
    Restored = 5,

    /// <summary>Đánh giá bị người dùng báo cáo.</summary>
    Reported = 6,

    /// <summary>Alias for Hidden when executed by Admin.</summary>
    AdminHidden = Hidden,

    /// <summary>Alias for Restored when executed by Admin.</summary>
    AdminRestored = Restored
}
