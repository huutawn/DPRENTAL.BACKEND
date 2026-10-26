using Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Services.Projections;

/// <summary>
/// Chiếu các event cấp item vào rental_item_facts. Mỗi event chỉ điền cột thời gian của riêng nó
/// (một lần) và đề xuất trạng thái mới qua <see cref="StatusGate"/>. Không gọi SaveChanges.
/// </summary>
public sealed class RentalItemProjector
{
    private readonly IRentalFactRepository _repository;
    private readonly ReadModelChangeSet _changes;

    public RentalItemProjector(IRentalFactRepository repository, ReadModelChangeSet changes)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    public async Task<Result> ApplyAsync(RentalItemConfirmedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.SupplierId ??= payload.SupplierId;
        item.ConfirmedAt ??= payload.DecisionAt.ToUniversalTime(); // → AcceptedCount
        StatusGate.Apply(item, ItemFactStatus.AwaitingHandover, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemRejectedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.SupplierId ??= payload.SupplierId;
        item.RejectedAt ??= payload.DecisionAt.ToUniversalTime(); // → RejectedCount
        StatusGate.Apply(item, ItemFactStatus.Rejected, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemExpiredPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.SupplierId ??= payload.SupplierId;
        item.ExpiredAt ??= payload.ExpiredAt.ToUniversalTime();
        item.ExpiryReason ??= payload.Reason; // SUPPLIER_CONFIRMATION_EXPIRED → ConfirmationTimeoutCount
        StatusGate.Apply(item, ItemFactStatus.Expired, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemCancelledPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.CancelledAt ??= payload.CancelledAt.ToUniversalTime();
        StatusGate.Apply(item, ItemFactStatus.Cancelled, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemHandoverCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.SupplierId ??= payload.SupplierId;
        item.Quantity ??= payload.Quantity;
        item.HandoverAt ??= payload.ActualHandoverAt.ToUniversalTime(); // → RentedQuantity
        StatusGate.Apply(item, ItemFactStatus.InRental, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemReturnedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.ReturnedAt ??= payload.ReturnsCompletedAt.ToUniversalTime();
        StatusGate.Apply(item, ItemFactStatus.PendingSettlement, payload.OrderVersion);
        return Result.Success();
    }

    /// <summary>
    /// Payload chưa có completedAt (chờ R2) nên dùng occurredAt của envelope, theo contract mục 4.
    /// </summary>
    public async Task<Result> ApplyAsync(RentalItemCompletedPayload payload, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, cancellationToken);
        item.CompletedAt ??= occurredAt.ToUniversalTime(); // → CompletedRentalValue, CompletedRentalCount
        StatusGate.Apply(item, ItemFactStatus.Completed, payload.OrderVersion);
        return Result.Success();
    }

    private async Task<RentalItemFact> GetItemAsync(Guid rentalItemId, Guid orderId, CancellationToken cancellationToken)
    {
        RentalItemFact item = await RentalFactRows.GetOrCreateItemAsync(_repository, rentalItemId, orderId, cancellationToken);
        _changes.TrackItem(item); // để báo SignalR cho đúng NCC sau khi lưu
        return item;
    }
}
