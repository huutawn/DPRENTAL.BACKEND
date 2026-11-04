using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>ThÃ´ng tin cÃ´ng khai cá»§a nhÃ  cung cáº¥p theo chá»§ listing.</summary>
[ApiController]
[Route("organizations/by-owner/{ownerUserId:guid}/supplier-contact")]
[AllowAnonymous]
public sealed class SupplierContactsByOwnerController(SupplierContactService service) : ControllerBase
{
    /// <summary>Xem thÃ´ng tin cÃ´ng khai cá»§a tá»• chá»©c Supplier Ä‘ang hoáº¡t Ä‘á»™ng, khÃ´ng yÃªu cáº§u Ä‘Äƒng nháº­p.</summary>
    [HttpGet]
    [ProducesResponseType<OrganizationPublicResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get(Guid ownerUserId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await service.GetByOwnerAsync(ownerUserId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
