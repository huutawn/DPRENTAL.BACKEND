using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dprental.RentalService.Application.IServices.Cancellation;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Người thuê (Renter) xem các đơn hàng của mình.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize(Policy = "Renter")]
[Route("orders")]
public sealed class RentalOrdersController(
    IRenterOrderQueryService orders,
    IRenterOrderCancellationService cancellation,
    IRenterDeliveryNoteService deliveryNotes,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Danh sách đơn hàng của người thuê.
    /// </summary>
    /// <param name="page">Số thứ tự trang (mặc định 1).</param>
    /// <param name="pageSize">Số đơn mỗi trang (mặc định 20, tối đa 100).</param>
    /// <param name="status">Trạng thái đơn cần lọc, tùy chọn.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy danh sách đơn thành công.</response>
    /// <response code="400">Trạng thái lọc không hợp lệ (Order.InvalidStatus).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân có quyền Renter.</response>
    /// <returns>Một trang đơn hàng của người thuê.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<RenterOrderSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMyOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        Result<PagedResponse<RenterOrderSummaryResponse>> result =
            await orders.GetMyOrdersAsync(page, pageSize, status, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Người thuê hủy cả đơn khi đang chờ thanh toán.
    /// </summary>
    /// <param name="orderId">Mã định danh đơn hàng.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="request">Lý do hủy, tùy chọn.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Hủy đơn thành công, đơn chuyển sang Cancelled.</response>
    /// <response code="400">Thiếu hoặc sai Idempotency-Key, hoặc lý do hủy quá dài.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân có quyền Renter.</response>
    /// <response code="404">Không tìm thấy đơn của người gọi (Order.NotFound).</response>
    /// <response code="409">Đơn không ở PendingPayment, hóa đơn đã thanh toán hoặc xung đột Idempotency-Key.</response>
    /// <response code="503">Hóa đơn đang được tạo hoặc việc giải phóng phân bổ chưa hoàn tất; thử lại cùng Idempotency-Key.</response>
    /// <returns>Kết quả hủy đơn.</returns>
    [HttpPost("{orderId:guid}/cancel")]
    [ProducesResponseType(typeof(CancelOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CancelMyOrder(
        [FromRoute] Guid orderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CancelOrderRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        Result<CancelOrderResponse> result =
            await cancellation.CancelOrderAsync(orderId, request, idempotencyKey, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Chi tiết một đơn hàng của người thuê.
    /// </summary>
    /// <param name="orderId">Mã định danh đơn hàng.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy chi tiết đơn thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân có quyền Renter.</response>
    /// <response code="404">Không tìm thấy đơn của người gọi (Order.NotFound).</response>
    /// <returns>Đơn cha kèm các đơn con và thiết bị.</returns>
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType(typeof(RenterOrderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        Result<RenterOrderDetailResponse> result = await orders.GetMyOrderAsync(orderId, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Sửa ghi chú giao hàng cho nhà cung cấp, trước khi máy được bàn giao.
    /// </summary>
    /// <param name="orderId">Mã định danh đơn hàng.</param>
    /// <param name="request">Ghi chú mới; null hoặc rỗng là xóa ghi chú.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Đã lưu ghi chú; trả về thông tin giao hàng của đơn.</response>
    /// <response code="400">Ghi chú quá 500 ký tự (Order.InvalidDeliveryNote).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân có quyền Renter.</response>
    /// <response code="404">Không tìm thấy đơn của người gọi (Order.NotFound).</response>
    /// <response code="409">Máy đã được bàn giao hoặc đơn đã kết thúc (Order.DeliveryNoteLocked), hoặc đơn vừa được cập nhật (Order.ConcurrencyConflict).</response>
    /// <returns>Thông tin giao hàng sau khi sửa.</returns>
    [HttpPatch("{orderId:guid}/delivery-note")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(OrderDeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateDeliveryNote(
        [FromRoute] Guid orderId,
        [FromBody] UpdateDeliveryNoteRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<OrderDeliveryResponse> result = await deliveryNotes.UpdateDeliveryNoteAsync(
            orderId, request, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
