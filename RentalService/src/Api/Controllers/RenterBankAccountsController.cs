using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.IServices.BankAccounts;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Sổ tài khoản ngân hàng nhận hoàn tiền của người thuê.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize(Policy = "Renter")]
[Route("me/bank-accounts")]
public sealed class RenterBankAccountsController(
    ICurrentRenter currentRenter,
    IRenterBankAccountService bankAccountService) : ControllerBase
{
    /// <summary>
    /// Lấy sổ tài khoản ngân hàng của người thuê.
    /// </summary>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Danh sách tài khoản, tài khoản mặc định đứng đầu.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <returns>Danh sách tài khoản ngân hàng.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RenterBankAccountResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await bankAccountService.ListAsync(currentRenter.RenterId, cancellationToken));

    /// <summary>
    /// Xem một tài khoản trong sổ tài khoản ngân hàng.
    /// </summary>
    /// <param name="bankAccountId">Mã tài khoản.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Thông tin tài khoản.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy tài khoản trong sổ của người thuê (BankAccount.NotFound).</response>
    /// <returns>Tài khoản ngân hàng.</returns>
    [HttpGet("{bankAccountId:guid}")]
    [ProducesResponseType(typeof(RenterBankAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute] Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        Result<RenterBankAccountResponse> result = await bankAccountService.GetAsync(
            currentRenter.RenterId, bankAccountId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>
    /// Thêm một tài khoản ngân hàng vào sổ tài khoản. Tài khoản đầu tiên tự thành mặc định.
    /// </summary>
    /// <param name="request">Thông tin tài khoản ngân hàng.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="201">Đã thêm tài khoản.</response>
    /// <response code="400">Dữ liệu tài khoản không hợp lệ (BankAccount.InvalidRequest).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="409">Tài khoản đã có trong sổ, sổ đã đủ 10 tài khoản, hoặc sổ đang được cập nhật bởi yêu cầu khác.</response>
    /// <returns>Tài khoản vừa thêm.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(RenterBankAccountResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateRenterBankAccountRequest request,
        CancellationToken cancellationToken)
    {
        Result<RenterBankAccountResponse> result = await bankAccountService.CreateAsync(
            currentRenter.RenterId, request, cancellationToken);

        return result.IsFailure
            ? this.ToProblem(result.Error)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Xóa một tài khoản khỏi sổ tài khoản. Các đơn đã đặt vẫn giữ tài khoản đã chép lúc đặt.
    /// </summary>
    /// <param name="bankAccountId">Mã tài khoản cần xóa.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Đã xóa tài khoản.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy tài khoản trong sổ của người thuê (BankAccount.NotFound).</response>
    /// <response code="409">Sổ tài khoản đang được cập nhật bởi yêu cầu khác.</response>
    [HttpDelete("{bankAccountId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        [FromRoute] Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        Result result = await bankAccountService.DeleteAsync(currentRenter.RenterId, bankAccountId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>
    /// Đặt một tài khoản làm tài khoản mặc định.
    /// </summary>
    /// <param name="bankAccountId">Mã tài khoản muốn đặt làm mặc định.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Tài khoản đã là mặc định.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy tài khoản trong sổ của người thuê (BankAccount.NotFound).</response>
    /// <response code="409">Sổ tài khoản đang được cập nhật bởi yêu cầu khác.</response>
    /// <returns>Tài khoản mặc định mới.</returns>
    [HttpPost("{bankAccountId:guid}/default")]
    [ProducesResponseType(typeof(RenterBankAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetDefault(
        [FromRoute] Guid bankAccountId,
        CancellationToken cancellationToken)
    {
        Result<RenterBankAccountResponse> result = await bankAccountService.SetDefaultAsync(
            currentRenter.RenterId, bankAccountId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
