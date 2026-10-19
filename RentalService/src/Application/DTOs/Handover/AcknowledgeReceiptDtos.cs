using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Application.DTOs.Handover;

/// <summary>
/// Yêu cầu Người thuê xác nhận đã nhận thiết bị ngoài công trường.
/// </summary>
/// <param name="Note">Ghi chú xác nhận của Người thuê (tùy chọn, tối đa 2000 ký tự).</param>
public sealed record AcknowledgeReceiptRequest(
    [MaxLength(2000)] string? Note = null);

/// <summary>
/// Kết quả xác nhận đã nhận thiết bị của Người thuê.
/// </summary>
/// <param name="ItemId">Mã định danh mục thuê.</param>
/// <param name="HandoverRecordId">Mã định danh bản ghi bàn giao.</param>
/// <param name="ReceivedAt">Thời điểm Người thuê xác nhận nhận máy.</param>
/// <param name="ReceivedBy">Mã định danh Người thuê xác nhận.</param>
/// <param name="Note">Ghi chú lúc xác nhận.</param>
public sealed record AcknowledgeReceiptResponse(
    Guid ItemId,
    Guid HandoverRecordId,
    DateTimeOffset ReceivedAt,
    Guid ReceivedBy,
    string? Note);
