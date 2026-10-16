using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;
/// <summary>
/// Thao tác của Nhà cung cấp (Supplier) trên từng mục thuê (RentalItem) sau khi đã nhận đơn.
/// </summary>
/// <remarks>
/// Nhận hoặc từ chối đơn làm theo cả đơn con tại <c>/supplier/sub-orders/{subOrderId}/accept|reject</c>.
/// Các thao tác yêu cầu gửi kèm header <c>Idempotency-Key</c> để đảm bảo tính an toàn chống trùng lặp.
/// </remarks>

[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("items")]
public sealed class RentalItemsController : ControllerBase
{
    private readonly ISupplierDecisionService _decisionService;
    private readonly IRentalCallerContext _callerContext;

    public RentalItemsController(
        ISupplierDecisionService decisionService,
        IRentalCallerContext callerContext)
    {
        _decisionService = decisionService ?? throw new ArgumentNullException(nameof(decisionService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Supplier bàn giao thiết bị cho mục thuê (RentalItem).
    /// </summary>
    /// <remarks>
    /// Mục thuê phải đang chờ bàn giao (<c>AWAITING_HANDOVER</c>) và thuộc Supplier đang gọi. Rental ghi nhận bàn giao
    /// rồi gửi lệnh <c>MarkAssetsHandedOver</c> sang Inventory qua Kafka; 202 nghĩa là đã nhận yêu cầu.
    /// Gửi lại cùng Idempotency-Key và nội dung trả lại kết quả đã lưu.
    /// </remarks>
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
}
