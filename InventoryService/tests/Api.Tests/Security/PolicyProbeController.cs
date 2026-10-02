using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Tests.Security;

/// <summary>
/// Controller kiểm thử nội bộ phục vụ kiểm chứng 5 authorization policies của DPR-147.
/// </summary>
[ApiController]
[Route("api/test")]
public sealed class PolicyProbeController : ControllerBase
{
    private readonly IAuthorizationService _authService;

    // Bộ nhớ in-memory giả lập danh sách tài sản phục vụ resource-based authorization
    public static readonly Dictionary<Guid, InventoryAsset> TestAssets = new();

    public PolicyProbeController(IAuthorizationService authService)
    {
        _authService = authService;
    }

    [HttpPost("assets/{assetId}/manage")]
    public async Task<IActionResult> ProbeAssetManage(Guid assetId)
    {
        if (!TestAssets.TryGetValue(assetId, out var asset))
        {
            return NotFound();
        }

        var result = await _authService.AuthorizeAsync(User, asset, InventoryPolicies.AssetManage);
        return result.Succeeded ? Ok(new { managed = true }) : Forbid();
    }

    [HttpGet("assets/{assetId}/calendar")]
    public async Task<IActionResult> ProbeCalendarRead(Guid assetId)
    {
        if (!TestAssets.TryGetValue(assetId, out var asset))
        {
            return NotFound();
        }

        var result = await _authService.AuthorizeAsync(User, asset, InventoryPolicies.CalendarRead);
        return result.Succeeded ? Ok(new { calendar = "detail" }) : Forbid();
    }

    [HttpGet("availability/sanitized")]
    [Authorize(Policy = InventoryPolicies.SanitizedAvailability)]
    public IActionResult ProbeSanitizedAvailability()
    {
        return Ok(new { availability = "sanitized" });
    }

    [HttpPost("allocations/commands")]
    [Authorize(Policy = InventoryPolicies.AllocationWrite)]
    public IActionResult ProbeAllocationCommands()
    {
        return Ok(new { commandExecuted = true });
    }

    [HttpPost("assets/{assetId}/manual-allocate")]
    public async Task<IActionResult> ProbeManualAllocate(Guid assetId)
    {
        if (!TestAssets.TryGetValue(assetId, out var asset))
        {
            return NotFound();
        }

        var result = await _authService.AuthorizeAsync(User, asset, InventoryPolicies.ManualAllocation);
        return result.Succeeded ? Ok(new { manuallyAllocated = true }) : Forbid();
    }
}
