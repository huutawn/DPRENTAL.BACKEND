using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API đọc và tìm kiếm khám phá danh mục EquipmentListing công khai (Public Equipment Listing Discovery).
/// Cho phép truy cập ẩn danh (AllowAnonymous), áp dụng whitelist tham số và bảo vệ an toàn dữ liệu.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("public/equipment-listings")]
public sealed class PublicEquipmentListingsController : ControllerBase
{
    private static readonly HashSet<string> AllowedListQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "q",
        "categoryId",
        "equipmentTypeId",
        "brandId",
        "provinceCode",
        "wardCode",
        "sort",
        "filters",
        "from",
        "to",
        "requestedQuantity",
        "page",
        "pageSize"
    };

    private readonly IPublicEquipmentListingService _publicListingService;
    private readonly IInventoryCallerContext _callerContext;

    public PublicEquipmentListingsController(
        IPublicEquipmentListingService publicListingService,
        IInventoryCallerContext callerContext)
    {
        _publicListingService = publicListingService ?? throw new ArgumentNullException(nameof(publicListingService));
        _callerContext = callerContext ?? throw new ArgumentNullException(nameof(callerContext));
    }

    /// <summary>
    /// Tìm kiếm và khám phá danh sách EquipmentListing công khai theo bộ lọc và tính khả dụng.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<PublicEquipmentListingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(
        [FromQuery] ListPublicEquipmentListingsRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Từ chối parameter lặp để tránh binding mơ hồ
        foreach (var key in Request.Query.Keys)
        {
            if (Request.Query[key].Count > 1)
            {
                return this.ToProblem(Error.Validation(
                    "INVALID_QUERY_PARAMETER",
                    $"Query parameter '{key}' bị lặp lại. Endpoint không chấp nhận tham số trùng lặp."));
            }
        }

        // 2. Trả 400 cho query parameter ngoài whitelist (loại trừ manufactureYear, ownerUserId, unit fields)
        var invalidQueryKeys = Request.Query.Keys
            .Where(k => !AllowedListQueryKeys.Contains(k))
            .ToList();

        if (invalidQueryKeys.Count > 0)
        {
            return this.ToProblem(Error.Validation(
                "INVALID_QUERY_PARAMETER",
                $"Query parameter '{invalidQueryKeys[0]}' không được hỗ trợ. Endpoint danh sách công khai chỉ chấp nhận: q, categoryId, equipmentTypeId, brandId, provinceCode, wardCode, sort, filters, from, to, requestedQuantity, page, pageSize."));
        }

        // 3. Yêu cầu from và to phải được cung cấp đồng thời; từ chối chuỗi rỗng
        bool hasFrom = Request.Query.ContainsKey("from");
        bool hasTo = Request.Query.ContainsKey("to");

        if (hasFrom != hasTo)
        {
            return this.ToProblem(Error.Validation(
                "INVALID_QUERY_PARAMETER",
                "Tham số 'from' và 'to' phải được cung cấp đồng thời."));
        }

        if (hasFrom && hasTo)
        {
            var fromRaw = Request.Query["from"].ToString();
            var toRaw = Request.Query["to"].ToString();

            if (string.IsNullOrWhiteSpace(fromRaw) || string.IsNullOrWhiteSpace(toRaw))
            {
                return this.ToProblem(Error.Validation(
                    "INVALID_QUERY_PARAMETER",
                    "Tham số ngày không được để trống."));
            }
        }

        // 4. requestedQuantity phải đi kèm from và to
        if (Request.Query.ContainsKey("requestedQuantity") && (!hasFrom || !hasTo))
        {
            return this.ToProblem(Error.Validation(
                "INVALID_QUERY_PARAMETER",
                "Tham số 'requestedQuantity' chỉ được sử dụng khi lọc theo khoảng thời gian 'from' và 'to'."));
        }

        var result = await _publicListingService.ListPublicListingsAsync(request, _callerContext, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        Response.Headers["X-Search-Backend"] = result.Value.SearchBackend;
        return Ok(result.Value.Value);
    }

    /// <summary>
    /// Lấy chi tiết thông tin một EquipmentListing công khai theo ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PublicEquipmentListingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var result = await _publicListingService.GetPublicListingByIdAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Kiểm tra tính khả dụng công khai của một EquipmentListing trong khoảng thời gian đề xuất.
    /// </summary>
    [HttpPost("{id:guid}/availability/check")]
    [ProducesResponseType(typeof(CheckListingAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckAvailability(
        [FromRoute] Guid id,
        [FromBody] CheckListingAvailabilityRequest request,
        [FromServices] IInventoryAvailabilityService availabilityService,
        CancellationToken cancellationToken)
    {
        var result = await availabilityService.CheckListingAvailabilityAsync(id, request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Thống kê số lượng listing đang hoạt động (Active) trên toàn hệ thống nhóm theo equipmentTypeId và brandId.
    /// </summary>
    [HttpGet("statistics")]
    [ProducesResponseType(typeof(EquipmentListingStatisticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetStatistics(CancellationToken cancellationToken = default)
    {

        if (Request.Query.Count != 0)
        {
            return this.ToProblem(Error.Validation("INVALID_QUERY_PARAMETER", "Tham số thống kê không hợp lệ."));
        }

        var result = await _publicListingService.GetStatisticsAsync(cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
