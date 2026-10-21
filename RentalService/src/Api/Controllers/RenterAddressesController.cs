using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.DTOs.Addresses;
using Dprental.RentalService.Application.IServices.Addresses;
using Dprental.RentalService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Sổ địa chỉ giao của người thuê.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Authorize(Policy = "Renter")]
[Route("me/addresses")]
public sealed class RenterAddressesController(
    ICurrentRenter currentRenter,
    IRenterAddressService addressService) : ControllerBase
{
    /// <summary>
    /// Lấy sổ địa chỉ giao của người thuê.
    /// </summary>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Danh sách địa chỉ của người thuê.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <returns>Danh sách địa chỉ giao.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RenterAddressResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await addressService.ListAsync(currentRenter.RenterId, cancellationToken));

    /// <summary>
    /// Xem một địa chỉ trong sổ địa chỉ.
    /// </summary>
    /// <param name="addressId">Mã địa chỉ.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Thông tin địa chỉ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy địa chỉ trong sổ của người thuê (Address.NotFound).</response>
    /// <returns>Địa chỉ giao.</returns>
    [HttpGet("{addressId:guid}")]
    [ProducesResponseType(typeof(RenterAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        [FromRoute] Guid addressId,
        CancellationToken cancellationToken)
    {
        Result<RenterAddressResponse> result = await addressService.GetAsync(
            currentRenter.RenterId, addressId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>
    /// Thêm một địa chỉ giao vào sổ địa chỉ.
    /// </summary>
    /// <param name="request">Thông tin địa chỉ giao.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="201">Đã thêm địa chỉ.</response>
    /// <response code="400">Dữ liệu địa chỉ không hợp lệ (Address.InvalidRequest).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="409">Sổ địa chỉ đã đủ 10 địa chỉ hoặc đang được cập nhật bởi yêu cầu khác.</response>
    /// <returns>Địa chỉ vừa thêm.</returns>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(RenterAddressResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] SaveRenterAddressRequest request,
        CancellationToken cancellationToken)
    {
        Result<RenterAddressResponse> result = await addressService.CreateAsync(
            currentRenter.RenterId, request, cancellationToken);

        return result.IsFailure
            ? this.ToProblem(result.Error)
            : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    /// <summary>
    /// Sửa một địa chỉ giao trong sổ địa chỉ.
    /// </summary>
    /// <param name="addressId">Mã địa chỉ cần sửa.</param>
    /// <param name="request">Thông tin địa chỉ mới.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Đã sửa địa chỉ.</response>
    /// <response code="400">Dữ liệu địa chỉ không hợp lệ (Address.InvalidRequest).</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy địa chỉ trong sổ của người thuê (Address.NotFound).</response>
    /// <response code="409">Sổ địa chỉ đang được cập nhật bởi yêu cầu khác.</response>
    /// <returns>Địa chỉ sau khi sửa.</returns>
    [HttpPatch("{addressId:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(RenterAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromRoute] Guid addressId,
        [FromBody] SaveRenterAddressRequest request,
        CancellationToken cancellationToken)
    {
        Result<RenterAddressResponse> result = await addressService.UpdateAsync(
            currentRenter.RenterId, addressId, request, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }

    /// <summary>
    /// Xóa một địa chỉ giao khỏi sổ địa chỉ.
    /// </summary>
    /// <param name="addressId">Mã địa chỉ cần xóa.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="204">Đã xóa địa chỉ.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy địa chỉ trong sổ của người thuê (Address.NotFound).</response>
    /// <response code="409">Sổ địa chỉ đang được cập nhật bởi yêu cầu khác.</response>
    [HttpDelete("{addressId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(
        [FromRoute] Guid addressId,
        CancellationToken cancellationToken)
    {
        Result result = await addressService.DeleteAsync(currentRenter.RenterId, addressId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }

    /// <summary>
    /// Đặt một địa chỉ làm địa chỉ mặc định.
    /// </summary>
    /// <param name="addressId">Mã địa chỉ muốn đặt làm mặc định.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <response code="200">Địa chỉ đã là mặc định.</response>
    /// <response code="401">Người dùng chưa được xác thực.</response>
    /// <response code="403">Người dùng không có quyền Renter.</response>
    /// <response code="404">Không tìm thấy địa chỉ trong sổ của người thuê (Address.NotFound).</response>
    /// <response code="409">Sổ địa chỉ đang được cập nhật bởi yêu cầu khác.</response>
    /// <returns>Địa chỉ mặc định mới.</returns>
    [HttpPost("{addressId:guid}/default")]
    [ProducesResponseType(typeof(RenterAddressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetDefault(
        [FromRoute] Guid addressId,
        CancellationToken cancellationToken)
    {
        Result<RenterAddressResponse> result = await addressService.SetDefaultAsync(
            currentRenter.RenterId, addressId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
