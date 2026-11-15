using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.IServices.Settlement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

[ApiController]
[Authorize]
[Route("damage-levels")]
public sealed class DamageLevelsController(IDamageLevelService damageLevels) : ControllerBase
{
    /// <summary>Bảng mức hư hại hiện hành để nhà cung cấp chọn mức cho từng máy khi đề nghị khấu trừ.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DamageLevelDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok(await damageLevels.GetAllAsync(cancellationToken));
}
