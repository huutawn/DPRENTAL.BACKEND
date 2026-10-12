using Dprental.RentalService.Api.Contracts.Checkout;
using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.Checkout;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize(Policy = "Renter")]
[Route("checkout")]
[Route("~/rental/checkout")]
public sealed class RentalCheckoutController(
    ICurrentRenter currentRenter,
    IRentalCallerContext callerContext,
    ICheckoutService checkout) : ControllerBase
{
    /// <summary>
    /// Gửi đơn hàng từ các dòng đã chọn trong giỏ. Gửi lại với cùng Idempotency-Key trả về đúng đơn đã tạo.
    /// </summary>
    /// <response code="201">Đơn hàng mới ở trạng thái chờ thanh toán, đã có hóa đơn.</response>
    /// <response code="200">Đơn hàng đã tạo từ lần gửi trước với cùng Idempotency-Key.</response>
    [HttpPost]
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

        CheckoutResult result = await checkout.SubmitAsync(
            new CheckoutRenterContext(currentRenter.RenterId, callerContext.IsProfileCompleted),
            request,
            idempotencyKey,
            cancellationToken);

        if (result.IsFailure)
        {
            return ToProblem(result);
        }

        CheckoutResponse body = CheckoutResponse.From(result.Order!, result.IsReplay);
        return result.IsReplay ? Ok(body) : StatusCode(StatusCodes.Status201Created, body);
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

        if (result.HoldConflicts.Count > 0)
        {
            details.Extensions["conflicts"] = result.HoldConflicts;
        }

        if (result.CurrentQuote is not null)
        {
            details.Extensions["currentQuote"] = CheckoutQuoteResponse.From(result.CurrentQuote);
        }

        if (result.PendingOrderCode is not null)
        {
            details.Extensions["pendingOrderCode"] = result.PendingOrderCode;
        }

        if (result.Order is not null)
        {
            details.Extensions["orderCode"] = result.Order.OrderCode;
            details.Extensions["paymentDeadline"] = result.Order.PaymentDeadline;
        }

        return problem;
    }
}
