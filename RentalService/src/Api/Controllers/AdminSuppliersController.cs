using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.IServices.BankAccounts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Quản trị viên (Admin) xem thông tin nhà cung cấp để chuyển tiền quyết toán.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize]
[Route("admin/suppliers")]
public sealed class AdminSuppliersController(
    IAdminSupplierBankAccountService bankAccounts,
    IRentalCallerContext callerContext) : ControllerBase
{
    /// <summary>
    /// Tài khoản mặc định của nhà cung cấp.
    /// </summary>
    /// <param name="supplierId">Mã nhà cung cấp, chính là user đại diện.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Tài khoản nhận tiền.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người gọi không phải Admin hoặc là service token.</response>
    /// <response code="404">Nhà cung cấp chưa có tài khoản mặc định (BankAccount.NotFound).</response>
    /// <returns>Tài khoản nhận tiền của nhà cung cấp.</returns>
    [HttpGet("{supplierId:guid}/bank-account")]
    [ProducesResponseType(typeof(AdminSupplierBankAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBankAccount(
        [FromRoute] Guid supplierId,
        CancellationToken cancellationToken = default)
    {
        Result<AdminSupplierBankAccountResponse> result =
            await bankAccounts.GetDefaultAsync(supplierId, callerContext, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }
}
