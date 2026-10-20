using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Kafka.Transport;

/// <summary>
/// Handler xử lý sự kiện SearchProjectionChanged nhận được từ Kafka topic inventory.projection.events.v1.
/// Validate chặt chẽ entity type, ID và action; ghi nhận công việc vào cơ sở dữ liệu với desired revision và cờ fanout.
/// Không gọi Meilisearch trực tiếp trong transaction của Inbox để đảm bảo tính chịu lỗi và phân tách trách nhiệm.
/// </summary>
public sealed class SearchProjectionMessageHandler : IMessageHandler<SearchProjectionChangedPayload>
{
    public const string ConsumerGroupId = "inventory-search-projection-consumer";

    private readonly ISearchProjectionStore _projectionStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SearchProjectionMessageHandler>? _logger;

    public SearchProjectionMessageHandler(
        ISearchProjectionStore projectionStore,
        TimeProvider? timeProvider = null,
        ILogger<SearchProjectionMessageHandler>? logger = null)
    {
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> HandleAsync(
        MessageEnvelope<SearchProjectionChangedPayload> envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = envelope.Payload;
        if (payload is null)
        {
            return Error.Validation("INVALID_PAYLOAD", "Payload không được để trống.");
        }

        if (payload.Revision <= 0)
            return Error.Validation("INVALID_REVISION", "Revision phải lớn hơn không.");

        // Validate entity type, ID và action tại consumer
        if (string.IsNullOrWhiteSpace(payload.EntityType) || payload.EntityId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Action))
        {
            return Error.Validation("INVALID_PAYLOAD", "Payload thiếu EntityType, EntityId hoặc Action.");
        }

        bool isListing = string.Equals(payload.EntityType, "EquipmentListing", StringComparison.OrdinalIgnoreCase);
        bool isAsset = string.Equals(payload.EntityType, "InventoryAsset", StringComparison.OrdinalIgnoreCase);
        if (!isListing && !isAsset)
        {
            return Error.Validation("UNSUPPORTED_ENTITY_TYPE", $"EntityType '{payload.EntityType}' không được hỗ trợ.");
        }

        bool isUpsert = string.Equals(payload.Action, SearchProjectionJob.ActionUpsert, StringComparison.OrdinalIgnoreCase);
        bool isDelete = string.Equals(payload.Action, SearchProjectionJob.ActionDelete, StringComparison.OrdinalIgnoreCase);
        if (!isUpsert && !isDelete)
        {
            return Error.Validation("UNSUPPORTED_ACTION", $"Action '{payload.Action}' không được hỗ trợ.");
        }

        var normalizedType = isListing ? "EquipmentListing" : "InventoryAsset";
        var normalizedAction = isUpsert ? SearchProjectionJob.ActionUpsert : SearchProjectionJob.ActionDelete;

        var now = _timeProvider.GetUtcNow();

        await _projectionStore.UpsertJobAsync(
            entityType: normalizedType,
            entityId: payload.EntityId,
            action: normalizedAction,
            desiredRevision: payload.Revision,
            needsAssetFanout: payload.NeedsAssetFanout,
            now: now,
            cancellationToken: cancellationToken);

        _logger?.LogDebug("Đã ghi nhận SearchProjectionJob: {EntityType} {EntityId} action={Action} rev={Revision} fanout={Fanout}",
            normalizedType, payload.EntityId, normalizedAction, payload.Revision, payload.NeedsAssetFanout);

        return Result.Success();
    }
}
