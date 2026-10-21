using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.IServices.Checkout;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Gửi đơn hàng (checkout) từ giỏ hàng thuê của người thuê (Renter).
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize(Policy = "Renter")]
[Route("checkout")]
[Route("~/rental/checkout")]
public sealed class RentalCheckoutController(
    ICurrentRenter currentRenter,
    IRentalCallerContext callerContext,
    ICheckoutService checkout) : ControllerBase
{
    /// <summary>
    /// Gửi đơn hàng từ các dòng đã chọn trong giỏ, hoặc thuê ngay một máy từ trang chi tiết.
    /// </summary>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="request">Các dòng giỏ được chọn, nơi giao, tài khoản nhận hoàn tiền, điều khoản và tổng tiền người thuê đã thấy.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="202">Đã nhận yêu cầu và gửi lệnh giữ lịch; theo dõi kết quả qua <c>statusUrl</c>. Gửi lại cùng Idempotency-Key trả lại đúng thao tác này.</response>
    /// <response code="400">Thiếu hoặc sai header Idempotency-Key, dữ liệu gửi lên không hợp lệ (Checkout.InvalidRequest) hoặc ngày thuê không còn hợp lệ (Checkout.InvalidRentalDates).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter hoặc chưa hoàn thiện hồ sơ (Checkout.ProfileIncomplete).</response>
    /// <response code="404">Không tìm thấy giỏ hàng, dòng giỏ đã chọn, hoặc địa chỉ trong sổ của người thuê (Address.NotFound).</response>
    /// <response code="409">Điều khoản đã đổi phiên bản, đang có lần gửi đơn đang xử lý hoặc đơn chưa thanh toán, thiết bị không còn cho thuê hoặc không đủ số lượng, giá thay đổi, hoặc Idempotency-Key đã dùng cho yêu cầu khác.</response>
    /// <response code="503">InventoryService tạm thời không trả được thông tin thiết bị (Checkout.DependencyUnavailable).</response>
    /// <returns>Mã thao tác gửi đơn và đường dẫn xem trạng thái.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(CheckoutOperationAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Submit(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CheckoutRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        if (idempotencyKey.Trim().Length > CheckoutRules.MaxIdempotencyKeyLength)
        {
            return this.ToProblem(Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key tối đa 64 ký tự."));
        }

        var result = await checkout.SubmitAsync(
            new CheckoutRenterContext(currentRenter.RenterId, callerContext.IsProfileCompleted),
            request,
            idempotencyKey,
            cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result.Failure!);
        }

        var accepted = result.Value;
        Response.Headers.Location = accepted.StatusUrl;
        return Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>
    /// Xem trạng thái một lần gửi đơn (checkout) đang xử lý bất đồng bộ.
    /// </summary>
    /// <param name="operationId">Mã lần gửi đơn trả về trong 202.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Trạng thái hiện tại của lần gửi đơn.</response>
    /// <response code="404">Không tìm thấy lần gửi đơn của người thuê này.</response>
    [HttpGet("operations/{operationId}")]
    [HttpGet("~/rental/checkout/operations/{operationId}")]
    [ProducesResponseType(typeof(CheckoutOperationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOperationStatus(
        string operationId,
        CancellationToken cancellationToken)
    {
        var result = await checkout.GetOperationStatusAsync(
            operationId,
            currentRenter.RenterId,
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>Gắn chi tiết của bước bị lỗi vào problem details để client hiển thị đúng chỗ.</summary>
    private ObjectResult ToProblem(CheckoutResult result)
    {
        ObjectResult problem = this.ToProblem(result.Error);
        if (problem.Value is not ProblemDetails details)
        {
            return problem;
        }

        details.Extensions["retryable"] = result.IsRetryable;

        if (result.FieldErrors.Count > 0)
        {
            details.Extensions["fieldErrors"] = result.FieldErrors;
        }

        if (result.LineIssues.Count > 0)
        {
            details.Extensions["lines"] = result.LineIssues;
        }

        if (result.CurrentQuote is not null)
        {
            details.Extensions["currentQuote"] = CheckoutQuoteResponse.From(result.CurrentQuote);
        }

        if (result.PendingOrderCode is not null)
        {
            details.Extensions["pendingOrderCode"] = result.PendingOrderCode;
        }

        return problem;
    }
}
