using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("internal/orders")]
public sealed class RentalInternalOperationsController : ControllerBase
{
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IRentalCallerContext _callerContext;

    public RentalInternalOperationsController(
        IRentalOperationExecutor operationExecutor,
        IRentalCallerContext callerContext)
    {
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Replay thủ công một operation bởi Admin qua route nội bộ (POST /api/v1/rental/internal/orders/{orderId}/operations/{operationId}/replay).
    /// </summary>
    [HttpPost("{orderId:guid}/operations/{operationId}/replay")]
    [ProducesResponseType(typeof(AdminReplayResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplayOperation(
        [FromRoute] Guid orderId,
        [FromRoute] string operationId,
        [FromBody] AdminReplayRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!_callerContext.IsAuthenticated)
        {
            return this.ToProblem(Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu đăng nhập."));
        }

        if (_callerContext.IsService)
        {
            return this.ToProblem(Error.Forbidden("SERVICE_TOKEN_FORBIDDEN", "Service token không được phép thực hiện thao tác replay của Admin."));
        }

        if (!_callerContext.IsAdmin() || !_callerContext.UserId.HasValue)
        {
            return this.ToProblem(Error.Forbidden("ADMIN_REQUIRED", "Chỉ tài khoản Admin mới có quyền replay operation."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return this.ToProblem(Error.Validation("MISSING_IDEMPOTENCY_KEY", "Header Idempotency-Key là bắt buộc."));
        }

        if (idempotencyKey.Trim().Length > 64)
        {
            return this.ToProblem(Error.Validation("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key tối đa 64 ký tự."));
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return this.ToProblem(Error.Validation("MISSING_REASON", "Lý do replay (reason) là bắt buộc."));
        }

        string trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length > 1000)
        {
            return this.ToProblem(Error.Validation("INVALID_REASON", "Lý do replay tối đa 1000 ký tự."));
        }

        Guid adminActorId = _callerContext.UserId.Value;

        var result = await _operationExecutor.ExecuteAdminReplayAsync(
            orderId,
            operationId,
            adminActorId,
            trimmedReason,
            idempotencyKey.Trim(),
            cancellationToken);

        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return StatusCode(StatusCodes.Status202Accepted, result.Value);
    }
}
