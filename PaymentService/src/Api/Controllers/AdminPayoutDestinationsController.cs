using Dprental.PaymentService.Api.Filters;
using Dprental.PaymentService.Api.Security;
using Dprental.PaymentService.Application.DTOs.Payout;
using Dprental.PaymentService.Application.IServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.PaymentService.Api.Controllers;

[ApiController]
[Route("api/v1/admin/suppliers/{supplierId:guid}/payout-destinations")]
[Authorize(Policy = PayoutPolicies.Admin)]
public sealed class AdminPayoutDestinationsController(ISupplierPayoutDestinationService service) : ControllerBase
{
    /// <summary>Admin đăng ký tài khoản nhận tiền sau khi xác minh ngoài hệ thống.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PayoutDestinationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Register(Guid supplierId, [FromBody] RegisterPayoutDestinationRequest request,
        CancellationToken ct)
    {
        if (!User.TryGetSubjectUserId(out var adminId) || adminId == Guid.Empty) return Forbid();
        var result = await service.RegisterAsync(supplierId, adminId, request, ct);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    /// <summary>Admin xem các phiên bản tài khoản nhận tiền đã xác minh.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PayoutDestinationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid supplierId, CancellationToken ct)
    {
        var result = await service.ListAsync(supplierId, ct);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
