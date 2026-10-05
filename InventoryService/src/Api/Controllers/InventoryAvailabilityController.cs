using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API truy vấn tính khả dụng và các khoảng bận đã làm sạch của tài sản theo DPR-150.
/// Yêu cầu quyền đọc Availability (Human authenticated hoặc Service có scope inventory.availability.read).
/// </summary>
[ApiController]
[Authorize(Policy = InventoryPolicies.AvailabilityRead)]
[Route("")]
public sealed class InventoryAvailabilityController : ControllerBase
{
    private readonly IInventoryAvailabilityService _availabilityService;

    public InventoryAvailabilityController(IInventoryAvailabilityService availabilityService)
    {
        _availabilityService = availabilityService ?? throw new ArgumentNullException(nameof(availabilityService));
    }

    /// <summary>
    /// Kiểm tra tính khả dụng hàng loạt tài sản trong khoảng thời gian đề xuất.
    /// Luôn đọc trực tiếp từ PostgreSQL.
    /// </summary>
    /// <param name="request">Thông tin danh sách assetIds và khoảng thời gian.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Danh sách các assetId khả dụng, không khả dụng và không tìm thấy.</returns>
    [HttpPost("availability/check")]
    [ProducesResponseType(typeof(CheckAvailabilityBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CheckAvailability(
        [FromBody] CheckAvailabilityBatchRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.CheckAvailabilityBatchAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Lấy danh sách các khoảng ngày bận đã được làm sạch của tài sản theo cửa sổ thời gian [from, to].
    /// Sử dụng Valkey read cache theo từng tháng với fallback PostgreSQL.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="from">Ngày bắt đầu cửa sổ truy vấn (inclusive).</param>
    /// <param name="to">Ngày kết thúc cửa sổ truy vấn (inclusive).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Dữ liệu trạng thái booking và các khoảng ngày bận.</returns>
    [HttpGet("assets/{assetId:guid}/busy-ranges")]
    [ProducesResponseType(typeof(AssetBusyRangesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBusyRanges(
        [FromRoute] Guid assetId,
        [FromQuery(Name = "from")] DateOnly? from,
        [FromQuery(Name = "to")] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var result = await _availabilityService.GetBusyRangesAsync(assetId, from, to, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
