using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Cancellation;
using Dprental.RentalService.Application.DTOs.Handover;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.IServices.Cancellation;
using Dprental.RentalService.Application.IServices.Handover;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;
/// <summary>
/// Thao tác trên từng mục thuê (RentalItem) sau khi đã nhận đơn: Nhà cung cấp bàn giao máy và Người thuê xác nhận nhận máy hoặc hủy khi máy không được giao.
/// </summary>

[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("items")]
public sealed class RentalItemsController : ControllerBase
{
    private readonly ISupplierDecisionService _decisionService;
    private readonly IHandoverAcknowledgementService _acknowledgementService;
    private readonly IItemCancellationService _cancellationService;
    private readonly IRentalCallerContext _callerContext;

    public RentalItemsController(
        ISupplierDecisionService decisionService,
        IHandoverAcknowledgementService acknowledgementService,
        IItemCancellationService cancellationService,
        IRentalCallerContext callerContext)
    {
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _acknowledgementService = acknowledgementService ?? throw new ArgumentNullException(nameof(acknowledgementService));
        _cancellationService = cancellationService ?? throw new ArgumentNullException(nameof(cancellationService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Supplier gửi bằng chứng bàn giao thiết bị trực tiếp cho mục thuê (RentalItem).
    /// Trả về HTTP 202 Accepted với trạng thái "Processing" khi bằng chứng đã được xác thực, snapshot được lưu
    /// và lệnh MarkAssetsHandedOver được đưa vào hàng đợi gửi sang InventoryService.
    /// Bàn giao chỉ chính thức thành công sau khi Inventory xác nhận qua Kafka event; lúc đó HandoverRecord
    /// mới được tạo và mục thuê chuyển sang InRental.
    /// Replay với cùng Idempotency-Key và payload trả lại trạng thái hiện có (Processing hoặc Succeeded kèm HandoverRecordId).
    /// </summary>
    /// <param name="itemId">Mã định danh mục thuê.</param>
    /// <param name="request">Thời điểm bàn giao thực tế (không được ở tương lai), ghi chú và tình trạng cùng ảnh của từng máy.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    [HttpPost("{itemId:guid}/handover")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(HandoverItemResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> HandoverItem(
        [FromRoute] Guid itemId,
        [FromBody] HandoverItemRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        var result = await _decisionService.HandoverItemAsync(itemId, request, idempotencyKey, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Accepted(result.Value);
    }

    /// <summary>
    /// Người thuê (Renter) xác nhận đã nhận thiết bị ngoài công trường.
    /// Yêu cầu mục thuê đã có HandoverRecord được tạo (sau khi Inventory xác nhận bàn giao).
    /// Thao tác là idempotent: replay trả về thông tin xác nhận ban đầu mà không ghi đè ghi chú,
    /// không thay đổi trạng thái item, không tăng version, không ghi thêm audit hay gửi notification lặp lại.
    /// </summary>
    /// <param name="itemId">Mã định danh mục thuê.</param>
    /// <param name="request">Ghi chú xác nhận của Người thuê (tùy chọn, tối đa 2000 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    [HttpPost("{itemId:guid}/acknowledge-receipt")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AcknowledgeReceiptResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcknowledgeReceipt(
        [FromRoute] Guid itemId,
        [FromBody] AcknowledgeReceiptRequest? request,
        CancellationToken cancellationToken = default)
    {
        var result = await _acknowledgementService.AcknowledgeAsync(itemId, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Người thuê (Renter) chủ động hủy mục thuê do thiết bị không được bàn giao đến công trình.
    /// Chỉ khả dụng từ 00:00 ngày D+1 theo giờ Việt Nam (sau ngày dự kiến bắt đầu thuê D)
    /// khi mục thuê còn ở trạng thái AwaitingHandover và không có lệnh bàn giao nào đang chờ xử lý hoặc đối soát.
    /// Khi hủy thành công, trả về HTTP 202 Accepted, chuyển mục thuê sang Cancelled,
    /// lên lịch nhả allocation và hoàn lại 100% tiền thuê cùng tiền cọc trong một transaction duy nhất.
    /// </summary>
    /// <param name="itemId">Mã định danh mục thuê.</param>
    /// <param name="request">Lý do hủy từ người thuê (tùy chọn, tối đa 500 ký tự).</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    [HttpPost("{itemId:guid}/cancel-not-delivered")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(CancelNotDeliveredResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelNotDelivered(
        [FromRoute] Guid itemId,
        [FromBody] CancelNotDeliveredRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        var result = await _cancellationService.CancelByRenterAsync(itemId, request, idempotencyKey, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Accepted(result.Value);
    }
}
