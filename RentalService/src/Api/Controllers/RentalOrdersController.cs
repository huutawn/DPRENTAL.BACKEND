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
    /// - page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100. Không có đơn vẫn trả 200 với items rỗng.
    /// </remarks>
    /// <param name="page">Số thứ tự trang (mặc định 1).</param>
    /// <param name="pageSize">Số đơn mỗi trang (mặc định 20, tối đa 100).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy danh sách đơn thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải tài khoản cá nhân có quyền Renter.</response>
    /// <returns>Một trang đơn hàng của người thuê.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<RenterOrderSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetMyOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        Result<PagedResponse<RenterOrderSummaryResponse>> result =
            await orders.GetMyOrdersAsync(page, pageSize, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Chi tiết một đơn hàng của người thuê.
    /// </summary>
    /// <remarks>
    /// - Trả đơn cha kèm thông tin nhận máy, mã hóa đơn, tổng tiền và các đơn con.
    /// - Mỗi đơn con là phần của một nhà cung cấp, có trạng thái tính từ các dòng và danh sách thiết bị đã chụp lúc đặt hàng.
    /// - Ngày thuê trả ra gồm ngày đầu và ngày dùng cuối (có tính), giống giỏ hàng.
    /// - Đơn không tồn tại hoặc của người khác đều trả 404 <c>Order.NotFound</c>.
    /// - Không trả tài khoản nhận hoàn tiền.
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
