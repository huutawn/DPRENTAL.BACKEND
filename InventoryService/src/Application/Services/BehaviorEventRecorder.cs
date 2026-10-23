using System.Globalization;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Contracts.Behavior;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

/// <summary>
/// Triển khai ghi nhận hành vi người dùng (ListingViewed, SearchPerformed) vào Outbox.
/// Tuân thủ:
/// - Ngân sách 200 ms lưu outbox, timeout nội bộ không làm thất bại thao tác đọc.
/// - Request cancellation vẫn lan truyền ra ngoài.
/// - Bỏ qua khi caller là Service JWT hoặc request đánh dấu prefetch.
/// - Chuẩn hóa timestamp kết thúc Z và serialize trường null bắt buộc.
/// </summary>
public sealed class BehaviorEventRecorder : IBehaviorEventRecorder
{
    private static readonly TimeSpan OutboxSaveTimeout = TimeSpan.FromMilliseconds(200);

    private readonly IOutboxStore _outboxStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BehaviorEventRecorder>? _logger;

    public BehaviorEventRecorder(
        IOutboxStore outboxStore,
        TimeProvider? timeProvider = null,
        ILogger<BehaviorEventRecorder>? logger = null)
    {
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task RecordListingViewedAsync(
        Guid listingId,
        Guid equipmentTypeId,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerContext);
        cancellationToken.ThrowIfCancellationRequested();

        if (listingId == Guid.Empty || equipmentTypeId == Guid.Empty)
        {
            return;
        }

        // Bỏ qua service principal và prefetch
        if (callerContext.HasServiceTokenClaim || callerContext.IsPrefetch)
        {
            return;
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        Guid? userId = (callerContext.IsHuman && callerContext.UserId.HasValue && callerContext.UserId.Value != Guid.Empty)
            ? callerContext.UserId.Value
            : null;

        var messageId = Guid.NewGuid();
        var correlationId = !string.IsNullOrWhiteSpace(callerContext.CorrelationId)
            ? callerContext.CorrelationId
            : messageId.ToString("D");

        // Key partition: human user ID nếu có; anonymous view dùng listing ID
        string kafkaKey = userId.HasValue ? userId.Value.ToString("D") : listingId.ToString("D");

        var payload = new ListingViewedPayload
        {
            UserId = userId,
            EquipmentListingId = listingId,
            EquipmentTypeId = equipmentTypeId
        };

        var envelope = new MessageEnvelope<ListingViewedPayload>
        {
            MessageId = messageId,
            MessageType = "ListingViewed",
            Version = 1,
            CorrelationId = correlationId,
            AggregateId = listingId.ToString("D"),
            OccurredAt = nowUtc,
            Producer = "InventoryService",
            Payload = payload
        };

        string wireJson = KafkaEnvelopeSerializer.Serialize(envelope, KafkaEnvelopeSerializer.BehaviorOptions);
        var outboxMessage = OutboxMessage.Create(
            messageId,
            KafkaTopics.InventoryBehaviorEventsV1,
            kafkaKey,
            wireJson,
            nowUtc);

        await SaveOutboxWithBudgetAsync(outboxMessage, cancellationToken);
    }

    public async Task RecordSearchPerformedAsync(
        ListPublicEquipmentListingsRequest request,
        int totalResultCount,
        IInventoryCallerContext callerContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callerContext);
        cancellationToken.ThrowIfCancellationRequested();

        // Chỉ ghi nhận trang 1
        int page = request.Page <= 0 ? 1 : request.Page;
        if (page > 1)
        {
            return;
        }

        // Bỏ qua service principal và prefetch
        if (callerContext.HasServiceTokenClaim || callerContext.IsPrefetch)
        {
            return;
        }

        // Kiểm tra xem request có chứa từ khóa hoặc bộ lọc hay không
        // "Có bộ lọc" gồm category, equipment type, brand, location, thông số hoặc khoảng ngày thuê; sort và các tham số phân trang không được tính là bộ lọc.
        bool hasFilter =
            !string.IsNullOrWhiteSpace(request.Q) ||
            request.CategoryId.HasValue ||
            request.EquipmentTypeId.HasValue ||
            request.BrandId.HasValue ||
            !string.IsNullOrWhiteSpace(request.ProvinceCode) ||
            !string.IsNullOrWhiteSpace(request.WardCode) ||
            !string.IsNullOrWhiteSpace(request.Filters) ||
            request.From.HasValue ||
            request.To.HasValue ||
            request.RequestedQuantity.HasValue;

        if (!hasFilter)
        {
            return;
        }

        var nowUtc = BusinessDateTime.NormalizeUtc(_timeProvider.GetUtcNow());
        Guid? userId = (callerContext.IsHuman && callerContext.UserId.HasValue && callerContext.UserId.Value != Guid.Empty)
            ? callerContext.UserId.Value
            : null;

        var messageId = Guid.NewGuid();
        var correlationId = !string.IsNullOrWhiteSpace(callerContext.CorrelationId)
            ? callerContext.CorrelationId
            : messageId.ToString("D");

        // Key partition: human user ID nếu có; anonymous search dùng message ID
        string kafkaKey = userId.HasValue ? userId.Value.ToString("D") : messageId.ToString("D");

        int pageSize = request.PageSize switch
        {
            <= 0 => 20,
            > 100 => 100,
            _ => request.PageSize
        };

        var filterContext = new SearchFilterContext
        {
            BrandId = request.BrandId,
            ProvinceCode = request.ProvinceCode,
            WardCode = request.WardCode,
            Sort = request.Sort,
            Filters = request.Filters,
            From = request.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = request.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            RequestedQuantity = request.RequestedQuantity,
            Page = 1,
            PageSize = pageSize
        };

        var payload = new SearchPerformedPayload
        {
            UserId = userId,
            EquipmentTypeId = request.EquipmentTypeId, // Chỉ ghi equipment type khi request chỉ định trực tiếp; không suy ra từ keyword/category
            Query = request.Q,
            CategoryId = request.CategoryId,
            FilterContext = filterContext,
            ResultCount = totalResultCount >= 0 ? totalResultCount : 0
        };

        var envelope = new MessageEnvelope<SearchPerformedPayload>
        {
            MessageId = messageId,
            MessageType = "SearchPerformed",
            Version = 1,
            CorrelationId = correlationId,
            AggregateId = messageId.ToString("D"),
            OccurredAt = nowUtc,
            Producer = "InventoryService",
            Payload = payload
        };

        string wireJson = KafkaEnvelopeSerializer.Serialize(envelope, KafkaEnvelopeSerializer.BehaviorOptions);
        var outboxMessage = OutboxMessage.Create(
            messageId,
            KafkaTopics.InventoryBehaviorEventsV1,
            kafkaKey,
            wireJson,
            nowUtc);

        await SaveOutboxWithBudgetAsync(outboxMessage, cancellationToken);
    }

    private async Task SaveOutboxWithBudgetAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(OutboxSaveTimeout);

        try
        {
            _outboxStore.Add(outboxMessage);
            await _outboxStore.SaveChangesAsync(linkedCts.Token);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Client canceled request.", ex, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Lưu behavior outbox message {MessageId} vượt quá ngân sách 200ms.", outboxMessage.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Lỗi khi lưu behavior outbox message {MessageId}.", outboxMessage.Id);
        }
    }

}
