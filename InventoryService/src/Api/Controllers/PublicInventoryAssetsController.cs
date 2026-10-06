using Dprental.InventoryService.Api.Filters;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.InventoryService.Api.Controllers;

/// <summary>
/// API đọc và tìm kiếm khám phá tài sản công khai (Public Asset Discovery) theo DPR-166.
/// Cho phép truy cập ẩn danh (AllowAnonymous), áp dụng whitelist tham số và bảo vệ an toàn dữ liệu.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("public/assets")]
public sealed class PublicInventoryAssetsController : ControllerBase
{
    private static readonly HashSet<string> AllowedListQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "q",
        "equipmentTypeId",
        "brandId",
        "manufactureYearFrom",
        "manufactureYearTo",
        "sort",
        "filters",
        "from",
        "to",
        "page",
        "pageSize"
    };

    private readonly IPublicInventoryAssetService _publicAssetService;

    public PublicInventoryAssetsController(IPublicInventoryAssetService publicAssetService)
    {
        _publicAssetService = publicAssetService ?? throw new ArgumentNullException(nameof(publicAssetService));
    }

    /// <summary>
    /// Tìm kiếm và khám phá danh sách tài sản công khai theo bộ lọc và khả dụng.
    /// </summary>
    /// <param name="request">Các tiêu chí tìm kiếm, lọc động và khả dụng công khai.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Danh sách tài sản phân trang đáp ứng điều kiện.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<PublicInventoryAssetResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(
        [FromQuery] ListPublicAssetsRequest request,
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

        // 2. Trả 400 cho query parameter ngoài whitelist (bao gồm ownerUserId, serialNumber, suspensionReason, acceptingBookings)
        var invalidQueryKeys = Request.Query.Keys
            .Where(k => !AllowedListQueryKeys.Contains(k))
            .ToList();

        if (invalidQueryKeys.Count > 0)
        {
            return this.ToProblem(Error.Validation(
                "INVALID_QUERY_PARAMETER",
                $"Query parameter '{invalidQueryKeys[0]}' không được hỗ trợ. Endpoint danh sách công khai chỉ chấp nhận: q, equipmentTypeId, brandId, manufactureYearFrom, manufactureYearTo, sort, filters, from, to, page, pageSize."));
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

        var result = await _publicAssetService.ListPublicAssetsAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Lấy chi tiết thông tin một tài sản công khai theo ID.
    /// </summary>
    /// <param name="assetId">Định danh tài sản.</param>
    /// <param name="cancellationToken">Token hủy yêu cầu.</param>
    /// <returns>Thông tin chi tiết tài sản đã được làm sạch bảo mật.</returns>
    [HttpGet("{assetId:guid}")]
    [ProducesResponseType(typeof(PublicInventoryAssetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var result = await _publicAssetService.GetPublicAssetByIdAsync(assetId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
