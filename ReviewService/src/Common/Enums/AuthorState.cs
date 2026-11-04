namespace Dprental.ReviewService.Common.Enums;

/// <summary>
/// Trạng thái của tác giả đối với đánh giá.
/// </summary>
public enum AuthorState
{
    /// <summary>Đánh giá đang hoạt động.</summary>
    Active = 1,

    /// <summary>Đánh giá đã bị tác giả rút lại.</summary>
    Withdrawn = 2
}
