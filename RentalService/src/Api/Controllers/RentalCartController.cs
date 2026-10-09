using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Api.Security;
using Dprental.RentalService.Application.Cart;
using Dprental.RentalService.Application.Cart.ReadModel;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("rental/cart")]
public sealed class RentalCartController(
    ICurrentRenter currentRenter,
    RentalCartUseCases cartUseCases,
    GetRentalCart getRentalCart) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        Result<RentalCartReadModel> result = await getRentalCart.ExecuteAsync(
            currentRenter.RenterId, cancellationToken);

        return result.IsFailure
            ? this.ToProblem(result.Error)
            : Ok(result.Value);
    }

    [HttpPost("items")]
    public async Task<IActionResult> Add(
        [FromBody] AddCartItemRequest request,
        CancellationToken cancellationToken)
    {
        var result = await cartUseCases.AddCartItemAsync(
            currentRenter.RenterId,
            request.EquipmentListingId,
            request.Quantity,
            request.ExpectedStartDate,
            request.ExpectedEndDate,
            cancellationToken);

        // Đã FIX: Trả về một Anonymous Object (hoặc DTO) chỉ chứa CartItemId để tránh lỗi JSON Cycle
        return result.IsFailure
            ? this.ToProblem(result.Error)
            : Ok(new { CartItemId = result.Value.Id });
    }


    [HttpPatch("items/{id:guid}")]
    public async Task<IActionResult> UpdateItem(
        [FromRoute] Guid id,
        [FromBody] UpdateCartItemRequest request,
        CancellationToken cancellationToken)
    {
        // Từ chối payload sai/thiếu
        if (request.Quantity is null && (request.ExpectedStartDate is null || request.ExpectedEndDate is null))
        {
            return BadRequest("Phải cung cấp Quantity hoặc cả ExpectedStartDate và ExpectedEndDate.");
        }

        if (request.Quantity is not null)
        {
            var qtyResult = await cartUseCases.UpdateCartItemQuantityAsync(
                currentRenter.RenterId, id, request.Quantity.Value, cancellationToken);

            if (qtyResult.IsFailure) return this.ToProblem(qtyResult.Error);
        }

        if (request.ExpectedStartDate is not null && request.ExpectedEndDate is not null)
        {
            var datesResult = await cartUseCases.UpdateCartItemDatesAsync(
                currentRenter.RenterId, id, request.ExpectedStartDate.Value, request.ExpectedEndDate.Value, cancellationToken);

            if (datesResult.IsFailure) return this.ToProblem(datesResult.Error);
        }

        return NoContent();
    }


    [HttpDelete("items/{id:guid}")]
    public async Task<IActionResult> Remove(
        Guid id,
        CancellationToken cancellationToken)
    {
        Result result = await cartUseCases.RemoveCartItemAsync(
            currentRenter.RenterId,
            id,
            cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }


    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken cancellationToken)
    {
        Result result = await cartUseCases.ClearCartAsync(
            currentRenter.RenterId, cancellationToken);

        return result.IsFailure ? this.ToProblem(result.Error) : NoContent();
    }
}
