using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Application.DTOs.Supplier;

/// <summary>
/// DTO yêu cầu từ chối mục thuê của Supplier.
/// </summary>
/// <param name="RejectReason">Lý do từ chối yêu cầu thuê (bắt buộc, tối đa 500 ký tự).</param>
/// <param name="OptionalNote">Ghi chú bổ sung tùy chọn (tối đa 2000 ký tự).</param>
public sealed record SupplierRejectRequest(
    [Required]
    [MaxLength(500)]
    string RejectReason,

    [MaxLength(2000)]
    string? OptionalNote = null);

/// <summary>
/// DTO phản hồi sau khi Supplier thực hiện quyết định (chấp nhận hoặc từ chối) cho một mục thuê.
/// </summary>
/// <param name="RentalItemId">Mã định danh duy nhất của mục thuê (RentalItemId).</param>
/// <param name="RentalOrderId">Mã định danh đơn thuê chứa mục này.</param>
/// <param name="OrderCode">Mã đơn thuê hiển thị cho người dùng.</param>
/// <param name="Status">Trạng thái mới của mục thuê sau quyết định (chuỗi PascalCase AwaitingHandover hoặc Rejected).</param>
/// <param name="DecisionAt">Thời điểm đưa ra quyết định.</param>
/// <param name="DecisionBy">Mã định danh người đưa ra quyết định (Supplier).</param>
/// <param name="DecisionReason">Lý do quyết định (nếu từ chối).</param>
/// <param name="DecisionNote">Ghi chú kèm theo quyết định (nếu có).</param>
public sealed record SupplierDecisionResponse(
    Guid RentalItemId,
    Guid RentalOrderId,
    string OrderCode,
    string Status,
    DateTimeOffset? DecisionAt,
    Guid? DecisionBy,
    string? DecisionReason,
    string? DecisionNote);

