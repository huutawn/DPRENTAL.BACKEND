namespace Dprental.ReviewService.Common.Enums;

/// <summary>
/// Trạng thái kiểm duyệt của quản trị viên đối với đánh giá.
/// </summary>
public enum ModerationState
{
    /// <summary>Đánh giá hiển thị công khai (nếu tác giả Active).</summary>
    Visible = 1,

    /// <summary>Đánh giá bị quản trị viên ẩn do vi phạm tiêu chuẩn.</summary>
    Hidden = 2
}
