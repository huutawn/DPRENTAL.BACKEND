using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API quản lý kho lưu trữ của bên cho thuê (Warehouse).
/// Mỗi bên cho thuê sở hữu duy nhất một kho cho toàn bộ listing và thiết bị của mình.
/// </summary>
[ApiController]
[Authorize]
[Route("warehouse")]
public sealed class WarehouseController : ControllerBase
{
    private readonly IWarehouseService _warehouseService;
    private readonly IInventoryCallerContext _callerContext;

    public WarehouseController(
        IWarehouseService warehouseService,
        IInventoryCallerContext callerContext)
    {
        _warehouseService = warehouseService ?? throw new ArgumentNullException(nameof(warehouseService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Đọc thông tin kho của bên cho thuê hiện tại (current user).
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(WarehouseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyWarehouse(CancellationToken cancellationToken)
    {
        var result = await _warehouseService.GetMyWarehouseAsync(_callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Tạo mới hoặc cập nhật toàn bộ địa chỉ kho của bên cho thuê hiện tại.
    /// Cập nhật nguyên tử, hỗ trợ kiểm tra xung đột ghi qua expectedVersion.
    /// </summary>
    [HttpPut("me")]
    [ProducesResponseType(typeof(WarehouseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateMyWarehouse(
        [FromBody] UpdateWarehouseRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _warehouseService.UpdateMyWarehouseAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
