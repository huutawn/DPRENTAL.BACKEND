using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.Operations;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;
/// <summary>
/// Quản lý các thao tác tác vụ nội bộ (Internal Operations) và phục hồi sự cố (Admin Replay).
/// </summary>
/// <remarks>
/// Cung cấp các endpoint nội bộ cho phép Quản trị viên (Admin) can thiệp, chạy lại (replay) các tác vụ
/// gặp lỗi hoặc bị gián đoạn trong vòng đời xử lý đơn hàng. Yêu cầu quyền Admin và từ chối Service token.
/// </remarks>

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
    /// Replay thủ công một tác vụ (operation) bởi Quản trị viên (Admin).
    /// </summary>
    /// <remarks>
    /// Quy trình nghiệp vụ replay tác vụ của Admin:
    /// - Xác thực người gọi: Yêu cầu đăng nhập, phải có vai trò <c>Admin</c>, từ chối Service token.
    /// - Kiểm tra <paramref name="idempotencyKey"/> (bắt buộc, tối đa 64 ký tự).
    /// - Kiểm tra lý do replay (<c>request.Reason</c> bắt buộc, tối đa 1000 ký tự) phục vụ nhật ký kiểm toán (audit log).
    /// - Đặt lại trạng thái trong biên nhận tác vụ (<c>RentalOperationReceipt</c>): <c>RetryCount = 0</c>, <c>Status = Pending</c>, <c>NextRetryAt = NOW()</c>.
    /// - Ghi nhận nhật ký kiểm toán hành động replay của Admin.
    /// - Trả về mã HTTP 202 Accepted kèm thông tin chi tiết tác vụ đã được lên lịch replay.
    /// </remarks>
    /// <param name="orderId">Mã định danh duy nhất của đơn thuê (RentalOrderId) liên quan đến tác vụ.</param>
    /// <param name="operationId">Mã định danh duy nhất của tác vụ (OperationId) cần replay.</param>
    /// <param name="request">Thông tin yêu cầu replay chứa lý do thực hiện thao tác của Admin.</param>
    /// <param name="idempotencyKey">Khóa chống trùng lặp gửi qua header HTTP (bắt buộc, tối đa 64 ký tự).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="202">Tác vụ replay đã được tiếp nhận thành công và xếp hàng để chạy lại.</response>
    /// <response code="400">Thiếu hoặc sai định dạng Idempotency-Key, hoặc lý do replay không hợp lệ/vượt quá 1000 ký tự.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không có quyền Admin hoặc là Service token.</response>
    /// <response code="404">Không tìm thấy đơn thuê hoặc tác vụ tương ứng.</response>
    /// <response code="409">Xung đột khóa Idempotency-Key với nội dung replay khác trước đó.</response>
    /// <response code="422">Tác vụ không ở trạng thái cho phép replay.</response>
    /// <returns>Dữ liệu chi tiết về tác vụ đã được lên lịch replay thành công.</returns>
    [HttpPost("{orderId:guid}/operations/{operationId}/replay")]
    [Consumes("application/json")]
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
