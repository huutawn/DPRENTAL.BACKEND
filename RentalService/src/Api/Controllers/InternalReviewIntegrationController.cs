#pragma warning disable CA1848, CA1859
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Controllers;

/// <summary>
/// Endpoints tích hợp nội bộ giữa RentalService và ReviewService.
/// Xác thực bảo mật strictly qua header X-Api-Key. Không chấp nhận Bearer JWT.
/// </summary>
[ApiController]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InternalReviewIntegrationController : ControllerBase
{
    private readonly IRentalOrderRepository _orderRepository;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InternalReviewIntegrationController>? _logger;

    public InternalReviewIntegrationController(
        IRentalOrderRepository orderRepository,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        ILogger<InternalReviewIntegrationController>? logger = null)
    {
        _orderRepository = orderRepository ?? throw new ArgumentNullException(nameof(orderRepository));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Lấy snapshot thông tin mục thuê phục vụ kiểm tra điều kiện đánh giá từ ReviewService.
    /// </summary>
    [HttpGet("/internal/orders/{orderId:guid}/items/{itemId:guid}/review-snapshot")]
    [HttpGet("internal/orders/{orderId:guid}/items/{itemId:guid}/review-snapshot")]
    [ProducesResponseType(typeof(RentalItemReviewSnapshotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviewSnapshot(
        [FromRoute] Guid orderId,
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken = default)
    {
        var authResult = ValidateInternalApiKey();
        if (authResult != null) return authResult;

        var snapshot = await _orderRepository.GetReviewSnapshotAsync(orderId, itemId, cancellationToken);
        if (snapshot == null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not Found",
                Detail = $"Không tìm thấy mục thuê '{itemId}' trong đơn hàng '{orderId}'.",
                Instance = HttpContext.Request.Path
            });
        }

        if (snapshot.Status != RentalItemStatus.Completed)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = $"Mục thuê '{itemId}' chưa ở trạng thái hoàn tất (COMPLETED). Trạng thái hiện tại: '{snapshot.Status}'.",
                Instance = HttpContext.Request.Path
            });
        }

        var dto = new RentalItemReviewSnapshotDto
        {
            OrderId = snapshot.OrderId,
            ItemId = snapshot.ItemId,
            RenterId = snapshot.RenterId,
            SupplierId = snapshot.SupplierId,
            EquipmentListingId = snapshot.EquipmentListingId,
            Status = snapshot.Status.ToString(),
            CompletedAt = snapshot.CompletedAt,
            OrderVersion = snapshot.OrderVersion
        };

        return Ok(dto);
    }

    /// <summary>
    /// Đối soát danh sách các mục thuê hoàn tất theo lô dựa trên Keyset Cursor Pagination.
    /// </summary>
    [HttpGet("/internal/reconciliation/rental-items")]
    [HttpGet("internal/reconciliation/rental-items")]
    [ProducesResponseType(typeof(ReconciliationBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetReconciliationBatch(
        [FromQuery] string? cursor = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var authResult = ValidateInternalApiKey();
        if (authResult != null) return authResult;
        var batchRecord = await _orderRepository.GetReviewReconciliationBatchAsync(
            cursor,
            limit,
            _timeProvider.GetUtcNow(),
            cancellationToken);

        var dtos = batchRecord.Items.Select(item => new RentalItemReviewSnapshotDto
        {
            OrderId = item.OrderId,
            ItemId = item.ItemId,
            RenterId = item.RenterId,
            SupplierId = item.SupplierId,
            EquipmentListingId = item.EquipmentListingId,
            Status = item.Status.ToString(),
            CompletedAt = item.CompletedAt,
            OrderVersion = item.OrderVersion
        }).ToList();

        return Ok(new ReconciliationBatchDto(dtos, batchRecord.NextCursor, batchRecord.HasMore));
    }

    private IActionResult? ValidateInternalApiKey()
    {
        // 1. Từ chối Bearer JWT token
        if (Request.Headers.TryGetValue("Authorization", out var auth) &&
            !string.IsNullOrWhiteSpace(auth.ToString()) &&
            auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Bearer tokens are not accepted for internal review integration endpoints. Use X-Api-Key.",
                instance: HttpContext.Request.Path);
        }

        // 2. Kiểm tra nếu middleware đã xác thực thành công
        if (HttpContext.Items.TryGetValue("InternalApiKeyVerified", out var verified) &&
            verified is true)
        {
            return null;
        }

        // 3. Fallback kiểm tra trực tiếp nếu middleware chưa chạy
        string? configuredKey = _configuration["InternalAuthentication:ApiKey"]
            ?? _configuration["INTERNAL_API_KEY"];

        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal Server Error",
                detail: "Internal API key is not configured.",
                instance: HttpContext.Request.Path);
        }

        if (!Request.Headers.TryGetValue("X-Api-Key", out var headerValues) ||
            headerValues.Count != 1 ||
            string.IsNullOrWhiteSpace(headerValues[0]))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Header 'X-Api-Key' is missing or has multiple values.",
                instance: HttpContext.Request.Path);
        }

        byte[] configuredBytes = Encoding.UTF8.GetBytes(configuredKey.Trim());
        byte[] providedBytes = Encoding.UTF8.GetBytes(headerValues[0]!.Trim());

        if (providedBytes.Length != configuredBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes))
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Invalid 'X-Api-Key' header.",
                instance: HttpContext.Request.Path);
        }

        HttpContext.Items["InternalApiKeyVerified"] = true;
        return null;
    }
}
