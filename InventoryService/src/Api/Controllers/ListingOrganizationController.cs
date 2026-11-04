using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

[ApiController]
[Authorize]
[Route("equipment-listings")]
public sealed class ListingOrganizationController(ListingOrganizationConfirmationService service, IInventoryCallerContext caller) : ControllerBase
{
    /// <summary>Chủ listing xác nhận tổ chức Supplier đang hoạt động của mình.</summary>
    [HttpPut("{id:guid}/organization")]
    [ProducesResponseType<ConfirmListingOrganizationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Confirm(Guid id, ConfirmListingOrganizationRequest request, CancellationToken cancellationToken)
    {
        var result = await service.ConfirmAsync(id, request.OrganizationId, caller, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(new ConfirmListingOrganizationResponse(result.Value));
    }
}
