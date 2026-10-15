using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.IServices.Operations;
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
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("internal/orders")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class RentalInternalOperationsController : ControllerBase
{
    private readonly IRentalOperationExecutor _operationExecutor;
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IRentalCallerContext _callerContext;

    public RentalInternalOperationsController(
        IRentalOperationExecutor operationExecutor,
        IRentalOrderRepository orderRepository,
        IRentalCallerContext callerContext)
    {
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
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
    /// - 202 chỉ xác nhận lên lịch; worker thực thi sau. operationId là chuỗi mã tác vụ đã lưu, không bắt buộc GUID.
    /// - Tác vụ đã Succeeded trả 409 OPERATION_ALREADY_SUCCEEDED; journal thiếu/hỏng trả 422 CORRUPTED_JOURNAL.
    /// - Retry cùng khóa/lý do được kiểm tra theo audit đã lưu; khóa dùng cho nội dung khác trả 409 IDEMPOTENCY_CONFLICT.
    /// <para>Ví dụ body: {"reason":"Phụ thuộc đã phục hồi, yêu cầu chạy lại"}.</para>
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
    /// <response code="409">Tác vụ đã thành công hoặc khóa Idempotency-Key được dùng với nội dung khác.</response>
    /// <response code="422">Journal bị thiếu hoặc hỏng, không thể replay an toàn.</response>
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

    [HttpGet("~/orders/{orderId:guid}/operations/{operationId}")]
    [HttpGet("{orderId:guid}/operations/{operationId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOperationReceipt(
        [FromRoute] Guid orderId,
        [FromRoute] string operationId,
        CancellationToken cancellationToken = default)
    {
        if (!_callerContext.IsAuthenticated)
        {
            return this.ToProblem(Error.Unauthorized("AUTH_REQUIRED", "Yêu cầu đăng nhập."));
        }

        var order = await _orderRepository.GetByIdWithDetailsAsync(orderId, cancellationToken);
        if (order is null)
        {
            return this.ToProblem(Error.NotFound("ORDER_NOT_FOUND", "Không tìm thấy đơn hàng."));
        }

        bool isRenter = order.RenterId == _callerContext.UserId;
        bool isSupplier = order.Items.Any(i => i.SupplierId == _callerContext.UserId);
        if (!_callerContext.IsAdmin() && !isRenter && !isSupplier)
        {
            return this.ToProblem(Error.Forbidden("FORBIDDEN", "Không có quyền xem tác vụ của đơn hàng này."));
        }

        var receipt = order.OperationReceipts.FirstOrDefault(r => string.Equals(r.OperationId, operationId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (receipt is null)
        {
            return this.ToProblem(Error.NotFound("OPERATION_NOT_FOUND", "Không tìm thấy tác vụ tương ứng."));
        }

        return Ok(new
        {
            receipt.Id,
            receipt.RentalOrderId,
            receipt.OperationId,
            receipt.Action,
            receipt.Status,
            receipt.IsSuccess,
            receipt.ErrorCode,
            receipt.ErrorMessage,
            receipt.CompletedAt,
            receipt.CreatedAt
        });
    }
}
