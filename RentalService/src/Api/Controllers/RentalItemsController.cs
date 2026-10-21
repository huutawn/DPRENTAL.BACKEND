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
    /// Supplier bàn giao thiết bị cho mục thuê (RentalItem).
    /// </summary>
    /// <param name="itemId">Mã định danh mục thuê.</param>
    /// <param name="request">Thời điểm bàn giao, ghi chú và tình trạng từng máy.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc).</param>
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
    /// </summary>
    /// <param name="itemId">Mã định danh mục thuê.</param>
    /// <param name="request">Lý do hủy từ người thuê (tùy chọn, tối đa 500 ký tự).</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc).</param>
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
