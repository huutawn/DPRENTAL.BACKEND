using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API quản lý tài sản trong kho (InventoryAsset) theo DPR-160.
/// </summary>
[ApiController]
[Authorize]
[Route("assets")]
public sealed class InventoryAssetsController : ControllerBase
{

    private readonly IInventoryAssetService _assetService;
    private readonly IInventoryCallerContext _callerContext;

    public InventoryAssetsController(
        IInventoryAssetService assetService,
        IInventoryCallerContext callerContext)
    {
        _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Tạo mới một tài sản trong kho thuộc sở hữu của người dùng hiện tại (JWT sub).
    /// </summary>
    /// <param name="request">Thông tin tài sản cần tạo.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Thông tin tài sản vừa tạo kèm header Location.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(InventoryAssetResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(
        [FromBody] CreateInventoryAssetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _assetService.CreateAssetAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        var asset = result.Value;
        return CreatedAtAction(nameof(GetById), new { assetId = asset.Id }, asset);
    }

    /// <summary>
    /// Lấy danh sách tài sản phân trang của người dùng hiện tại (JWT sub).
    /// </summary>
    /// <param name="page">Số trang (mặc định: 1, >= 1).</param>
    /// <param name="pageSize">Kích thước trang (mặc định: 20, 1 - 100).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Danh sách tài sản phân trang.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<InventoryAssetResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // List chỉ chấp nhận page và pageSize; query khác trả 400
        var invalidQueryKeys = Request.Query.Keys
            .Where(k => !string.Equals(k, "page", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(k, "pageSize", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (invalidQueryKeys.Count > 0)
        {
            return this.ToProblem(Error.Validation(
                "INVALID_QUERY_PARAMETER",
                $"Query parameter '{invalidQueryKeys[0]}' không được hỗ trợ. Endpoint danh sách chỉ chấp nhận 'page' và 'pageSize'."));
        }

        var result = await _assetService.ListAssetsAsync(page, pageSize, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Lấy chi tiết thông tin một tài sản theo ID.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Thông tin chi tiết tài sản.</returns>
    [HttpGet("{assetId:guid}")]
    [ProducesResponseType(typeof(InventoryAssetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var result = await _assetService.GetAssetByIdAsync(assetId, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Cập nhật thông tin chi tiết của tài sản theo chuẩn PATCH presence tracking.
    /// </summary>
    /// <param name="assetId">Định danh tài sản cần cập nhật.</param>
    /// <param name="request">Các trường cần cập nhật.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Thông tin tài sản sau khi cập nhật.</returns>
    [HttpPatch("{assetId:guid}")]
    [ProducesResponseType(typeof(InventoryAssetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Patch(
        Guid assetId,
        [FromBody] PatchInventoryAssetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _assetService.PatchAssetAsync(assetId, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Bật hoặc tắt tính năng nhận đặt lịch của tài sản.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="request">Cờ nhận đặt lịch và lý do tạm dừng (nếu tắt).</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Thông tin tài sản sau khi cập nhật trạng thái nhận đặt lịch.</returns>
    [HttpPatch("{assetId:guid}/accepting-bookings")]
    [ProducesResponseType(typeof(InventoryAssetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ToggleAcceptingBookings(
        Guid assetId,
        [FromBody] ToggleAcceptingBookingsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _assetService.ToggleAcceptingBookingsAsync(assetId, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
