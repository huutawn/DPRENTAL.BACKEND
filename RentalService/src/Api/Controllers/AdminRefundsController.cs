using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.DTOs.Refunds;
using Dprental.RentalService.Application.IServices.Refunds;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Quản trị viên (Admin) xem các đơn yêu cầu hoàn tiền để chuyển khoản cho người thuê.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("admin/refunds")]
public sealed class AdminRefundsController(
    IAdminRefundQueryService refunds,
    IAdminRefundCommandService refundCommands,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Danh sách đơn yêu cầu hoàn tiền.
    /// </summary>
    /// <remarks>
    /// Mỗi dòng là một dòng thiết bị: bị hủy, bị nhà cung cấp từ chối hoặc quá hạn sau khi đã thanh toán thì hoàn
    /// toàn bộ tiền thuê và cọc (<c>FULL_REFUND</c>); đã trả máy và chốt khấu trừ thì hoàn phần cọc còn lại
    /// (<c>DEPOSIT_RETURN</c>, bỏ qua khi bằng 0). Khoản chuyển sang <c>COMPLETED</c> khi Admin xác nhận đã chuyển khoản.
    /// Chờ hoàn xếp cũ nhất trước; đã hoàn và tất cả xếp mới nhất trước.
    /// </remarks>
    /// <param name="status">PENDING (chờ hoàn) hoặc COMPLETED (đã hoàn); bỏ trống là tất cả.</param>
    /// <param name="page">Số thứ tự trang (mặc định 1).</param>
    /// <param name="pageSize">Số khoản mỗi trang (mặc định 20, tối đa 100).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Một trang khoản cần hoàn.</response>
    /// <response code="400">Trạng thái lọc không hợp lệ (Order.InvalidStatus).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải Admin hoặc là service token.</response>
    /// <returns>Một trang đơn yêu cầu hoàn tiền.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<AdminRefundQueueItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetRefunds(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        Result<PagedResponse<AdminRefundQueueItemResponse>> result =
            await refunds.GetRefundsAsync(status, page, pageSize, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>
    /// Xác nhận đã chuyển khoản hoàn tiền cho người thuê.
    /// </summary>
    /// <remarks>
    /// Admin chuyển khoản theo tài khoản của khoản hoàn rồi gọi API này kèm mã giao dịch trên sao kê. Số tiền và tài khoản
    /// được chép vào xác nhận. Gửi lại cùng mã giao dịch cho cùng khoản trả kết quả đã lưu; người thuê thấy đơn đã hoàn tiền.
    /// </remarks>
    /// <param name="rentalItemId">Mã dòng thiết bị của khoản hoàn.</param>
    /// <param name="request">Mã giao dịch ngân hàng và ghi chú.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Đã xác nhận; trả khoản hoàn kèm xác nhận.</response>
    /// <response code="400">Thiếu mã giao dịch, mã hoặc ghi chú quá dài (Refund.InvalidRequest).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải Admin hoặc là service token.</response>
    /// <response code="404">Dòng thiết bị không có khoản nào cần hoàn (Refund.NotFound).</response>
    /// <response code="409">Khoản đã được xác nhận với mã khác (Refund.AlreadyConfirmed), mã giao dịch đã dùng (Refund.DuplicateBankTransactionRef) hoặc đơn đang được cập nhật (Refund.Busy).</response>
    /// <returns>Khoản hoàn sau khi xác nhận.</returns>
    [HttpPost("{rentalItemId:guid}/complete")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AdminOrderRefundResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmTransfer(
        [FromRoute] Guid rentalItemId,
        [FromBody] ConfirmRefundTransferRequest? request,
        CancellationToken cancellationToken = default)
    {
        Result<AdminOrderRefundResponse> result =
            await refundCommands.ConfirmTransferAsync(rentalItemId, request, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
