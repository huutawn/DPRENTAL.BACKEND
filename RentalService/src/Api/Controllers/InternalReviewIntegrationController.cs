#pragma warning disable CA1848, CA1859
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Common.Enums;
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
    private readonly RentalServiceDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InternalReviewIntegrationController>? _logger;

    public InternalReviewIntegrationController(
        RentalServiceDbContext dbContext,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        ILogger<InternalReviewIntegrationController>? logger = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
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

        var order = await _dbContext.RentalOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order == null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not Found",
                Detail = $"Không tìm thấy đơn thuê với mã '{orderId}'.",
                Instance = HttpContext.Request.Path
            });
        }

        var item = order.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not Found",
                Detail = $"Mục thuê '{itemId}' không thuộc về đơn hàng '{orderId}'.",
                Instance = HttpContext.Request.Path
            });
        }

        if (item.Status != RentalItemStatus.Completed)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = $"Mục thuê '{itemId}' chưa ở trạng thái hoàn tất (COMPLETED). Trạng thái hiện tại: '{item.Status}'.",
                Instance = HttpContext.Request.Path
            });
        }

        // Mốc thời gian hoàn tất nghiệp vụ authoritative: lấy từ chuyển đổi trạng thái SettlementCompleted
        // hoặc FinancialClosureConfirmedAt khi PaymentService xác nhận quyết toán thành công.
        // Tuyệt đối KHÔNG suy diễn từ thời điểm trả máy (ReturnsCompletedAt).
        var transition = await _dbContext.RentalStateTransitions
            .AsNoTracking()
            .Where(t => t.RentalItemId == item.Id && (t.ToState == "Completed" || t.Action == "SettlementCompleted"))
            .OrderByDescending(t => t.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

        DateTimeOffset? completedAt = transition?.OccurredAt ?? item.FinancialClosureConfirmedAt;

        if (completedAt == null)
        {
            var orderTransition = await _dbContext.RentalStateTransitions
                .AsNoTracking()
                .Where(t => t.RentalOrderId == order.Id && t.ToState == "Completed")
                .OrderByDescending(t => t.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken);

            completedAt = orderTransition?.OccurredAt;
        }

        var snapshot = new RentalItemReviewSnapshotDto
        {
            OrderId = order.Id,
            ItemId = item.Id,
            RenterId = order.RenterId,
            SupplierId = item.SupplierId,
            EquipmentListingId = item.EquipmentListingId,
            Status = item.Status.ToString(),
            CompletedAt = completedAt,
            OrderVersion = order.Version
        };

        return Ok(snapshot);
    }

    /// <summary>
    /// Đối soát danh sách các mục thuê hoàn tất theo lô dựa trên Keyset Cursor Pagination.
    /// </summary>
    [HttpGet("/internal/reconciliation/rental-items")]
    [HttpGet("internal/reconciliation/rental-items")]
    [HttpGet("/internal/orders/review-reconciliation")]
    [HttpGet("internal/orders/review-reconciliation")]
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

        int pageSize = limit <= 0 ? 100 : Math.Min(limit, 100);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset windowStart = now.AddDays(-30);
        DateTimeOffset? lastCompletedAt = null;
        Guid? lastItemId = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("WindowStart", out var wsProp) || root.TryGetProperty("windowStart", out wsProp))
                {
                    if (wsProp.TryGetDateTimeOffset(out var ws)) windowStart = ws;
                }
                if (root.TryGetProperty("LastCompletedAt", out var lcaProp) || root.TryGetProperty("lastCompletedAt", out lcaProp))
                {
                    if (lcaProp.TryGetDateTimeOffset(out var lca)) lastCompletedAt = lca;
                }
                if (root.TryGetProperty("LastItemId", out var liProp) || root.TryGetProperty("lastItemId", out liProp))
                {
                    if (liProp.TryGetGuid(out var li)) lastItemId = li;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to decode reconciliation cursor {Cursor}", cursor);
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Bad Request",
                    Detail = "Cursor không đúng định dạng.",
                    Instance = HttpContext.Request.Path
                });
            }
        }

        if (lastItemId.HasValue && !lastCompletedAt.HasValue)
        {
            var lastItem = await _dbContext.RentalItems
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == lastItemId.Value, cancellationToken);
            if (lastItem != null)
            {
                lastCompletedAt = lastItem.FinancialClosureConfirmedAt;
            }
        }

        // Lọc theo trạng thái Completed và cửa sổ hoàn tất (completed_at)
        var query = _dbContext.RentalItems
            .AsNoTracking()
            .Include(i => i.RentalOrder)
            .Where(i => i.Status == RentalItemStatus.Completed &&
                        i.FinancialClosureConfirmedAt != null &&
                        i.FinancialClosureConfirmedAt >= windowStart &&
                        i.FinancialClosureConfirmedAt <= now);

        if (lastCompletedAt.HasValue && lastItemId.HasValue)
        {
            query = query.Where(i =>
                i.FinancialClosureConfirmedAt > lastCompletedAt.Value ||
                (i.FinancialClosureConfirmedAt == lastCompletedAt.Value && i.Id.CompareTo(lastItemId.Value) > 0));
        }

        var rawItems = await query
            .OrderBy(i => i.FinancialClosureConfirmedAt)
            .ThenBy(i => i.Id)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        bool hasMore = rawItems.Count > pageSize;
        var pageItems = rawItems.Take(pageSize).ToList();

        string? nextCursor = null;
        if (hasMore && pageItems.Count > 0)
        {
            var last = pageItems[^1];
            var payload = new
            {
                WindowStart = windowStart,
                LastCompletedAt = last.FinancialClosureConfirmedAt,
                LastItemId = last.Id
            };
            string nextCursorJson = JsonSerializer.Serialize(payload);
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(nextCursorJson));
        }

        var dtos = pageItems.Select(item => new RentalItemReviewSnapshotDto
        {
            OrderId = item.RentalOrderId,
            ItemId = item.Id,
            RenterId = item.RentalOrder?.RenterId ?? Guid.Empty,
            SupplierId = item.SupplierId,
            EquipmentListingId = item.EquipmentListingId,
            Status = item.Status.ToString(),
            CompletedAt = item.FinancialClosureConfirmedAt,
            OrderVersion = item.RentalOrder?.Version ?? 1
        }).ToList();

        return Ok(new ReconciliationBatchDto(dtos, nextCursor, hasMore));
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
