using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Quản trị viên (Admin) tra cứu mọi đơn thuê.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("admin/orders")]
public sealed class AdminRentalOrdersController(
    IAdminOrderQueryService orders,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Tìm đơn thuê.
    /// </summary>
    /// <param name="request">Điều kiện tìm và phân trang.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Tìm thành công.</response>
    /// <response code="400">Trạng thái (Order.InvalidStatus), khoảng ngày (Order.InvalidDateRange) hoặc mã GUID không hợp lệ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải Admin hoặc là service token.</response>
    /// <returns>Một trang đơn khớp điều kiện.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<AdminOrderSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SearchOrders(
        [FromQuery] AdminOrderSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        Result<PagedResponse<AdminOrderSummaryResponse>> result =
            await orders.SearchOrdersAsync(request, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Chi tiết đầy đủ một đơn thuê.
    /// </summary>
    /// <param name="orderId">Mã định danh đơn.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Lấy chi tiết thành công.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải Admin hoặc là service token.</response>
    /// <response code="404">Không tìm thấy đơn (Order.NotFound).</response>
    /// <returns>Chi tiết đơn cho Admin.</returns>
    [HttpGet("{orderId:guid}")]
    [ProducesResponseType(typeof(AdminOrderDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(
        [FromRoute] Guid orderId,
        CancellationToken cancellationToken = default)
    {
        Result<AdminOrderDetailResponse> result = await orders.GetOrderAsync(orderId, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
