using System.Diagnostics.CodeAnalysis;

namespace Dprental.ReviewService.Application.DTOs.Reviews;

/// <summary>
/// DTO phân trang chuẩn cho các danh sách phản hồi (RFC 9110 / Allowlist).
/// </summary>
/// <typeparam name="T">Kiểu phần tử trong danh sách.</typeparam>
/// <param name="Items">Danh sách các phần tử của trang hiện tại.</param>
/// <param name="Page">Số thứ tự trang hiện tại (bắt đầu từ 1).</param>
/// <param name="PageSize">Kích thước trang (số phần tử tối đa trên một trang).</param>
/// <param name="TotalCount">Tổng số phần tử trên toàn bộ danh sách.</param>
/// <param name="TotalPages">Tổng số trang.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages)
{
    /// <summary>
    /// Khởi tạo mặc định phục vụ deserialization.
    /// </summary>
    public PagedResult()
        : this([], 1, 20, 0, 0)
    {
    }

    /// <summary>
    /// Tạo mới một PagedResult và tự động tính toán TotalPages.
    /// </summary>
    /// <param name="items">Danh sách các phần tử.</param>
    /// <param name="page">Số thứ tự trang.</param>
    /// <param name="pageSize">Kích thước trang.</param>
    /// <param name="totalCount">Tổng số phần tử.</param>
    /// <returns>Đối tượng PagedResult đã tính toán.</returns>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Convenient factory method on generic PagedResult record.")]
    public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(1, pageSize);
        var totalPages = totalCount <= 0 ? 0 : (int)Math.Ceiling((double)totalCount / safePageSize);

        return new PagedResult<T>(
            items,
            safePage,
            safePageSize,
            totalCount,
            totalPages);
    }
}
