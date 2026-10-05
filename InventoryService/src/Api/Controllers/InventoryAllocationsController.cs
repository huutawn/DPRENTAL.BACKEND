using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API quản lý giữ chỗ và phân bổ tài sản (Hold &amp; Allocation Core) theo DPR-151.
/// Yêu cầu Service Principal với scope inventory.allocation.write.
/// </summary>
[ApiController]
[Authorize(Policy = InventoryPolicies.AllocationWrite)]
[Route("api/v1/inventory")]
public sealed class InventoryAllocationsController : ControllerBase
{
    private readonly IInventoryAllocationService _allocationService;
    private readonly IInventoryCallerContext _callerContext;

    public InventoryAllocationsController(
        IInventoryAllocationService allocationService,
        IInventoryCallerContext callerContext)
    {
        _allocationService = allocationService ?? throw new ArgumentNullException(nameof(allocationService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Tạo mới một lượt giữ chỗ tạm thời (Hold) hoặc trả về lượt hiện tại nếu idempotent replay.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="request">Thông tin giữ chỗ tài sản.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>201 Created khi tạo mới; 200 OK khi replay.</returns>
    [HttpPost("assets/{assetId:guid}/holds")]
    [ProducesResponseType(typeof(AllocationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(AllocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateHold(
        Guid assetId,
        [FromBody] CreateHoldRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _allocationService.CreateHoldAsync(assetId, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return result.Value.IsReplay
            ? Ok(result.Value.Allocation)
            : StatusCode(StatusCodes.Status201Created, result.Value.Allocation);
    }

    /// <summary>
    /// Chuyển đổi lượt giữ chỗ còn hạn sang trạng thái phân bổ chính thức (Held -&gt; Allocated).
    /// </summary>
    /// <param name="allocationId">Định danh lượt phân bổ cần chuyển trạng thái.</param>
    /// <param name="request">Thông tin yêu cầu gồm operationKey bắt buộc.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>200 OK trả về thông tin phân bổ hiện tại.</returns>
    [HttpPost("allocations/{allocationId:guid}/allocate")]
    [ProducesResponseType(typeof(AllocationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Allocate(
        Guid allocationId,
        [FromBody] AllocateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _allocationService.AllocateAsync(allocationId, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
