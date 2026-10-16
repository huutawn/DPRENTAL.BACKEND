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
/// <remarks>
/// Admin tìm đơn theo mã đơn, đơn con, dòng, thiết bị, máy cụ thể, nhà cung cấp, người thuê, trạng thái và ngày đặt;
/// xem chi tiết kèm mã nội bộ (hóa đơn, thanh toán, giữ lịch, hoàn tiền, quyết toán), máy cụ thể và lịch sử trạng thái.
/// Chỉ Admin cá nhân; service token bị từ chối.
/// </remarks>
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
    /// <remarks>
    /// - Mọi tham số đều tùy chọn và kết hợp với nhau bằng AND; không tham số nào là mọi đơn, mới nhất trước.
    /// - <c>orderCode</c>, <c>subOrderCode</c> khớp chính xác, không phân biệt hoa thường.
    /// - <c>rentalItemId</c>, <c>listingId</c>, <c>assetId</c>, <c>supplierId</c>, <c>itemStatus</c> phải cùng khớp trên
    ///   một dòng: ví dụ supplierId + itemStatus=IN_RENTAL chỉ ra đơn có chính dòng của nhà cung cấp đó đang thuê.
    /// - <c>status</c> là trạng thái đơn (<c>PAID</c>…), <c>itemStatus</c> là trạng thái dòng (<c>IN_RENTAL</c>…).
    /// - <c>createdFrom</c> (gồm) và <c>createdTo</c> (không gồm) là thời điểm ISO 8601 bắt buộc có Z hoặc ±HH:mm
    ///   (dấu + cần URL-encode thành %2B).
    /// - page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100.
    /// </remarks>
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
    /// <remarks>
    /// - Toàn bộ đơn con và dòng của mọi nhà cung cấp, kèm InvoiceRef, PaymentRef, AllocationGroupId,
    ///   InventoryConfirmationRef, RefundRef, SettlementRef và các máy cụ thể (assetId, allocationId).
    /// - <c>history</c>: lịch sử trạng thái của đơn và từng dòng, cũ nhất trước, kèm người thực hiện và lý do.
    /// - Số tài khoản hoàn tiền chỉ hiện 4 số cuối. Mỗi lần xem được ghi log kèm mã Admin.
    /// </remarks>
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
