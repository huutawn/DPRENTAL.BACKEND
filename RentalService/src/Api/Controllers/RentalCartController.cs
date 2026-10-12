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
[Authorize(Policy = "Renter")]
[Route("cart")]
[Route("~/rental/cart")]
public sealed class RentalCartController(
    ICurrentRenter currentRenter,
    RentalCartUseCases cartUseCases,
    GetRentalCart getRentalCart) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] Dictionary<Guid, string>? clientPriceVersions, CancellationToken cancellationToken)
    {
        Result<RentalCartReadModel> result = await getRentalCart.ExecuteAsync(
            currentRenter.RenterId, clientPriceVersions, cancellationToken);

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
        bool hasAnyDate = request.ExpectedStartDate is not null || request.ExpectedEndDate is not null;
        bool hasBothDates = request.ExpectedStartDate is not null && request.ExpectedEndDate is not null;
        bool hasQuantity = request.Quantity is not null;

        if ((hasAnyDate && !hasBothDates) || hasQuantity == hasBothDates)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Cart.InvalidUpdate",
                Detail = "Cung cấp Quantity hoặc cả ExpectedStartDate và ExpectedEndDate; mỗi PATCH chỉ cập nhật một loại thông tin."
            });
        }

        if (hasQuantity)
        {
            var qtyResult = await cartUseCases.UpdateCartItemQuantityAsync(
                currentRenter.RenterId, id, request.Quantity.GetValueOrDefault(), cancellationToken);

            if (qtyResult.IsFailure) return this.ToProblem(qtyResult.Error);
        }
        else
        {
            var datesResult = await cartUseCases.UpdateCartItemDatesAsync(
                currentRenter.RenterId, id, request.ExpectedStartDate.GetValueOrDefault(), request.ExpectedEndDate.GetValueOrDefault(), cancellationToken);

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
