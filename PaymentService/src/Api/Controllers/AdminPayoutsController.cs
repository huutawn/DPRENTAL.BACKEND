using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.DTOs.Operations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/admin/payouts")]
[Authorize(Policy = PayoutPolicies.Admin)]
public sealed class AdminPayoutsController : ControllerBase
{
    private readonly ISupplierPayoutService _service;
    private readonly IFinancialReadService _financialRead;

    public AdminPayoutsController(ISupplierPayoutService service, IFinancialReadService financialRead)
    {
        _service = service;
        _financialRead = financialRead;
    }

    /// <summary>Admin xem payout; mặc định chỉ hiện khoản đang chờ.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(FinancialPage<AdminPayoutListItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] Guid? supplierId, [FromQuery] FinancialListQuery query, CancellationToken ct)
    {
        var result = await _financialRead.ListAdminPayoutsAsync(supplierId, query, ct);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>Gắn mã phiên bản tài khoản nhận tiền; cần đối chiếu hồ sơ Payment trước khi dùng thật.</summary>
    [HttpPost("{payoutId:guid}/destination")]
    [ProducesResponseType(typeof(AttachPayoutDestinationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AttachDestination(
        Guid payoutId, [FromBody] AttachPayoutDestinationRequest request, CancellationToken ct)
    {
        if (!TryGetAdminId(out var adminId)) return Forbid();
        var result = await _service.AttachDestinationAsync(payoutId, adminId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>Xác nhận admin đã chuyển khoản payout ngoài hệ thống.</summary>
    [HttpPost("{payoutId:guid}/complete")]
    [ProducesResponseType(typeof(CompleteSupplierPayoutResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(
        Guid payoutId, [FromBody] CompleteSupplierPayoutRequest request, CancellationToken ct)
    {
        if (!TryGetAdminId(out var adminId)) return Forbid();
        var result = await _service.CompleteAsync(payoutId, adminId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    private bool TryGetAdminId(out Guid adminId) =>
        User.TryGetSubjectUserId(out adminId) && adminId != Guid.Empty;
}
