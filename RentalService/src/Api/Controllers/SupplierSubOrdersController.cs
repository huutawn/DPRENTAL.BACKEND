using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Supplier;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.IServices.SupplierDecision;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Nhà cung cấp (Supplier) xem, nhận hoặc từ chối các đơn con của mình.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("supplier/sub-orders")]
public sealed class SupplierSubOrdersController(
    ISupplierSubOrderQueryService subOrders,
    ISupplierDecisionService decisions,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Danh sách đơn con của nhà cung cấp.
    /// </summary>
    /// <param name="page">Số thứ tự trang (mặc định 1).</param>
    /// <param name="pageSize">Số đơn con mỗi trang (mặc định 20, tối đa 100).</param>
    /// <param name="status">Trạng thái dòng cần lọc, tùy chọn.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy danh sách đơn con thành công.</response>
    /// <response code="400">Trạng thái lọc không hợp lệ (Order.InvalidStatus).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân (ví dụ service token).</response>
    /// <returns>Một trang đơn con của nhà cung cấp.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<SupplierSubOrderSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMySubOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        Result<PagedResponse<SupplierSubOrderSummaryResponse>> result =
            await subOrders.GetMySubOrdersAsync(page, pageSize, status, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Chi tiết một đơn con của nhà cung cấp.
    /// </summary>
    /// <param name="subOrderId">Mã định danh đơn con.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy chi tiết đơn con thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân (ví dụ service token).</response>
    /// <response code="404">Không tìm thấy đơn con của người gọi (SubOrder.NotFound).</response>
    /// <returns>Đơn con kèm thiết bị của nhà cung cấp.</returns>
    [HttpGet("{subOrderId:guid}")]
    [ProducesResponseType(typeof(SupplierSubOrderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMySubOrder(
        [FromRoute] Guid subOrderId,
        CancellationToken cancellationToken = default)
    {
        Result<SupplierSubOrderDetailResponse> result =
            await subOrders.GetMySubOrderAsync(subOrderId, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Supplier nhận cả đơn con.
    /// </summary>
    /// <param name="subOrderId">Mã định danh đơn con cần nhận.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Nhận đơn con thành công, mọi mục thuê chuyển sang AwaitingHandover.</response>
    /// <response code="400">Thiếu hoặc sai định dạng header Idempotency-Key.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="404">Không tìm thấy đơn con của người gọi (SUB_ORDER_NOT_FOUND).</response>
    /// <response code="409">Đơn chưa thanh toán, đơn con đã được quyết định, đang có thao tác nhận khác, hold chưa được gia hạn hoặc xung đột khóa Idempotency-Key.</response>
    /// <response code="410">Đơn con đã quá hạn xác nhận (SUPPLIER_DEADLINE_EXPIRED).</response>
    /// <response code="422">InventoryService trả kết quả phân bổ không khớp với đơn hàng.</response>
    /// <response code="503">InventoryService tạm thời không khả dụng hoặc đang chờ thử lại; gửi lại cùng Idempotency-Key.</response>
    /// <returns>Quyết định nhận cho cả đơn con.</returns>
    [HttpPost("{subOrderId:guid}/accept")]
    [ProducesResponseType(typeof(SubOrderDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AcceptSubOrder(
        [FromRoute] Guid subOrderId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        Result<SubOrderDecisionResponse> result =
            await decisions.AcceptSubOrderAsync(subOrderId, idempotencyKey, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Supplier từ chối cả đơn con.
    /// </summary>
    /// <param name="subOrderId">Mã định danh đơn con cần từ chối.</param>
    /// <param name="request">Lý do từ chối (bắt buộc) và ghi chú tùy chọn.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Từ chối đơn con thành công, mọi mục thuê chuyển sang Rejected.</response>
    /// <response code="400">Thiếu header Idempotency-Key, thiếu lý do từ chối hoặc dữ liệu không hợp lệ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="404">Không tìm thấy đơn con của người gọi (SUB_ORDER_NOT_FOUND).</response>
    /// <response code="409">Đơn chưa thanh toán, đơn con đã được quyết định, đang có thao tác nhận hoặc xung đột khóa Idempotency-Key.</response>
    /// <response code="410">Đơn con đã quá hạn xác nhận (SUPPLIER_DEADLINE_EXPIRED).</response>
    /// <returns>Quyết định từ chối cho cả đơn con.</returns>
    [HttpPost("{subOrderId:guid}/reject")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(SubOrderDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<IActionResult> RejectSubOrder(
        [FromRoute] Guid subOrderId,
        [FromBody] SupplierRejectRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        Result<SubOrderDecisionResponse> result =
            await decisions.RejectSubOrderAsync(subOrderId, request, idempotencyKey, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
