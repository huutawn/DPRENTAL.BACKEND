using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API quản lý danh mục niêm yết thiết bị (EquipmentListing) và đơn vị thiết bị vật lý trong kho.
/// </summary>
[ApiController]
[Authorize]
[Route("equipment-listings")]
public sealed class EquipmentListingsController : ControllerBase
{
    private readonly IEquipmentListingService _listingService;
    private readonly ICalendarService _calendarService;
    private readonly IInventoryCallerContext _callerContext;

    public EquipmentListingsController(
        IEquipmentListingService listingService,
        ICalendarService calendarService,
        IInventoryCallerContext callerContext)
    {
        _listingService = listingService ?? throw new ArgumentNullException(nameof(listingService));
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }
    /// <summary>
    /// Tạo mới một EquipmentListing ở trạng thái Draft cùng một thiết bị vật lý ban đầu qua số serial.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(EquipmentListingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create(
        [FromBody] CreateEquipmentListingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _listingService.CreateListingAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        var listing = result.Value;
        return CreatedAtAction(nameof(GetById), new { id = listing.Id }, listing);
    }

    /// <summary>
    /// Lấy danh sách EquipmentListing phân trang của người dùng hiện tại (My Listings).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<EquipmentListingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ListEquipmentListingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _listingService.ListOwnerListingsAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers["X-Search-Backend"] = result.Value.SearchBackend;
        return Ok(result.Value.Value);
    }

    /// <summary>
    /// Lấy chi tiết thông tin một EquipmentListing theo ID kèm derived totalQuantity.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(EquipmentListingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var result = await _listingService.GetListingByIdAsync(id, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Đọc lịch tổng hợp dùng chung của EquipmentListing (tối đa 6 tháng).
    /// </summary>
    [HttpGet("{id:guid}/calendar")]
    [ProducesResponseType(typeof(ListingCalendarResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCalendar(
        Guid id,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        var result = await _calendarService.GetListingCalendarAsync(id, from, to, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Cập nhật một phần thông tin chung hoặc trạng thái của EquipmentListing (PATCH).
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(EquipmentListingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Patch(
        Guid id,
        [FromBody] PatchEquipmentListingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _listingService.PatchListingAsync(id, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Cập nhật giá thuê niêm yết của EquipmentListing (thay thế toàn bộ).
    /// </summary>
    [HttpPut("{id:guid}/rental-rate")]
    [ProducesResponseType(typeof(RentalRateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateRentalRate(
        Guid id,
        [FromBody] RentalRateDto request,
        CancellationToken cancellationToken)
    {
        var result = await _listingService.UpdateRentalRateAsync(id, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Lấy danh sách phân trang các đơn vị thiết bị vật lý thuộc một EquipmentListing.
    /// </summary>
    [HttpGet("{id:guid}/assets")]
    [ProducesResponseType(typeof(PagedResponse<InventoryAssetResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetListingAssets(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _listingService.GetListingAssetsAsync(id, page, pageSize, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Thêm một đơn vị thiết bị vật lý vào một EquipmentListing hiện có bằng số serial.
    /// </summary>
    [HttpPost("{id:guid}/assets")]
    [ProducesResponseType(typeof(InventoryAssetResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddAsset(
        Guid id,
        [FromBody] AddListingAssetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _listingService.AddAssetAsync(id, request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return CreatedAtAction(
            actionName: nameof(InventoryAssetsController.GetById),
            controllerName: "InventoryAssets",
            routeValues: new { assetId = result.Value.Id },
            value: result.Value);
    }
}
