using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Api.Filters;
using Dprental.RentalService.Application.DTOs.ReviewIntegration;
using Dprental.RentalService.Application.IServices.ReviewIntegration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Controller nội bộ cung cấp snapshot và đối soát phục vụ ReviewService.
/// Xác thực qua X-Api-Key tại InternalApiKeyMiddleware; không yêu cầu Bearer JWT token.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Route("~/internal/orders")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class RentalInternalReviewController : ControllerBase
{
    private readonly IRentalReviewIntegrationService _reviewIntegrationService;

    public RentalInternalReviewController(IRentalReviewIntegrationService reviewIntegrationService)
    {
        _reviewIntegrationService = reviewIntegrationService ?? throw new ArgumentNullException(nameof(reviewIntegrationService));
    }

    /// <summary>
    /// Lấy snapshot thông tin mục thuê phục vụ kiểm tra điều kiện đánh giá từ ReviewService.
    /// </summary>
    /// <param name="orderId">Mã đơn thuê.</param>
    /// <param name="itemId">Mã mục thuê.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AllowAnonymous]
    [HttpGet("{orderId:guid}/items/{itemId:guid}/review-snapshot")]
    [ProducesResponseType(typeof(RentalItemReviewSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviewSnapshot(
        [FromRoute] Guid orderId,
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var result = await _reviewIntegrationService.GetReviewSnapshotAsync(orderId, itemId, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }

    /// <summary>
    /// Lấy danh sách đối soát mục thuê đã Completed trong cửa sổ 30 ngày theo keyset pagination.
    /// </summary>
    /// <param name="cursor">Cursor cố định cửa sổ và vị trí trang (base64 token).</param>
    /// <param name="limit">Số lượng tối đa mỗi trang (mặc định 100, tối đa 500).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [AllowAnonymous]
    [HttpGet("review-reconciliation")]
    [ProducesResponseType(typeof(RentalReviewReconciliationBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetReviewReconciliation(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await _reviewIntegrationService.GetReconciliationBatchAsync(cursor, limit, cancellationToken);
        if (result.IsFailure)
        {
            return this.ToProblem(result.Error);
        }

        return Ok(result.Value);
    }
}
