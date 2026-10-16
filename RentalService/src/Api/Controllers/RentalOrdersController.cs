using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Người thuê (Renter) xem các đơn hàng của mình.
/// </summary>
/// <remarks>
/// Người thuê xem đơn cha: toàn bộ đơn con (mỗi nhà cung cấp một đơn con) và mọi thiết bị trong đơn.
/// Người thuê được định danh qua token JWT (claim sub / Renter policy) và chỉ xem được đơn của chính mình.
/// </remarks>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize(Policy = "Renter")]
[Route("orders")]
public sealed class RentalOrdersController(
    IRenterOrderQueryService orders,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Danh sách đơn hàng của người thuê.
    /// </summary>
    /// <remarks>
    /// - Chỉ gồm đơn của người gọi, sắp xếp mới nhất trước.
    /// - Mỗi đơn có tổng tiền thuê, tổng tiền cọc, số dòng thiết bị và số đơn con.
    /// - Lọc theo <c>status</c> với giá trị như API trả ra, ví dụ <c>PENDING_PAYMENT</c>; bỏ trống là mọi trạng thái.
    /// - page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100. Không có đơn vẫn trả 200 với items rỗng.
    /// </remarks>
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
    /// Chi tiết một đơn hàng của người thuê.
    /// </summary>
    /// <remarks>
    /// - Trả đơn cha kèm thông tin nhận máy, tài khoản nhận hoàn tiền, mã hóa đơn, tình trạng thanh toán, tổng tiền và các đơn con.
    /// - Mỗi đơn con là phần của một nhà cung cấp, có trạng thái tính từ các dòng.
    /// - Mỗi dòng có thông tin thiết bị và giá đã chụp lúc đặt hàng (không đọc lại Listing hiện tại), trạng thái,
    ///   tình trạng hoàn tiền, biên bản bàn giao, các lần trả máy và quyết toán tiền cọc.
    /// - <c>paymentState</c>: <c>PENDING</c>, <c>PAID</c>, <c>EXPIRED</c> (đóng mà không thanh toán) hoặc
    ///   <c>LATE_PAYMENT_REFUND</c> (trả sau hạn, đang hoàn toàn bộ).
    /// - <c>refund.state</c> của dòng: <c>NONE</c>, <c>PROCESSING</c> (đã yêu cầu hoàn) hoặc <c>COMPLETED</c>.
    /// - Ngày thuê trả ra gồm ngày đầu và ngày dùng cuối (có tính), giống giỏ hàng.
    /// - Đơn không tồn tại hoặc của người khác đều trả 404 <c>Order.NotFound</c>.
    /// - Không trả máy cụ thể (AssetId), mã giữ lịch hay mã xác nhận nội bộ của Inventory.
    /// </remarks>
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
}
