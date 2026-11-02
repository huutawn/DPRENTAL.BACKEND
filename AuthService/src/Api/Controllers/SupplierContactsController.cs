using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Thông tin liên hệ tối thiểu của nhà cung cấp đang hoạt động.</summary>
[ApiController]
[Route("organizations/{organizationId:guid}/supplier-contact")]
[AllowAnonymous]
public sealed class SupplierContactsController(SupplierContactService service) : ControllerBase
{
    /// <summary>Xem tên và số điện thoại liên hệ của nhà cung cấp đang hoạt động.</summary>
    [HttpGet]
    [ProducesResponseType<SupplierContactResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid organizationId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await service.GetAsync(organizationId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
