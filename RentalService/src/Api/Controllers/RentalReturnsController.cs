using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.IServices.Returns;
using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("items/{itemId:guid}/returns")]
public sealed class RentalReturnsController(IReturnService returns, IRentalCallerContext caller) : ControllerBase
{
    /// <summary>Supplier ghi nhận thu hồi một hoặc nhiều máy đã bàn giao.</summary>
    /// <remarks>
    /// Gửi Idempotency-Key (tối đa 64 ký tự). Cùng khóa và nội dung trả lại kết quả đã lưu;
    /// đổi nội dung với cùng khóa trả 409. Chỉ supplier sở hữu item được ghi nhận.
    /// ActualReturnAt là giờ thực tế; SubmittedAt là giờ server. Còn máy chưa trả thì item giữ IN_RENTAL.
    /// Khi trả đủ và có máy hỏng, hạn gửi khấu trừ là SubmittedAt của lần cuối cộng 48 giờ.
    /// Thành công chỉ xác nhận lưu bằng chứng, chưa xác nhận Inventory đã giải phóng máy hoặc Payment đã quyết toán.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(RecordReturnResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecordReturn(Guid itemId, [FromBody] RecordReturnRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
    {
        var result = await returns.RecordReturnAsync(itemId, caller, request, idempotencyKey ?? string.Empty, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
