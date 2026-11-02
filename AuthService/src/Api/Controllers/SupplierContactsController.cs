using Dprental.AuthService.Api.Filters;
using Dprental.AuthService.Api.Security;
using Dprental.AuthService.Application.DTOs.Organization;
using Dprental.AuthService.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.AuthService.Api.Controllers;

/// <summary>Thông tin liên hệ tối thiểu của nhà cung cấp đang hoạt động.</summary>
[ApiController]
[Route("organizations/{organizationId:guid}/supplier-contact")]
[Authorize]
public sealed class SupplierContactsController(SupplierContactService service) : ControllerBase
{
    /// <summary>Người dùng đã đăng nhập xem tên và số điện thoại liên hệ của nhà cung cấp.</summary>
    [HttpGet]
    [ProducesResponseType<SupplierContactResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid organizationId, CancellationToken cancellationToken)
    {
        if (User.GetJwtSub() is not Guid) return Unauthorized();
        Response.Headers.CacheControl = "private, no-store";
        var result = await service.GetAsync(organizationId, cancellationToken);
        return result.IsFailure ? this.ToProblem(result.Error) : Ok(result.Value);
    }
}
