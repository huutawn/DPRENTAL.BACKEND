using Dprental.AnalyticsService.Application.Events.Rental;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Chiếu event cấp item vào analytics_rental_items. Mỗi event chỉ điền cột thời gian của riêng nó (một lần)
/// và đề xuất trạng thái mới qua <see cref="StatusGate"/>. Không gọi SaveChanges.
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

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, payload.SupplierId, cancellationToken);
        item.ConfirmedAt ??= payload.DecisionAt.ToUniversalTime(); // → AcceptedCount
        StatusGate.Apply(item, ItemStatuses.AwaitingHandover, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemRejectedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, payload.SupplierId, cancellationToken);
        item.RejectedAt ??= payload.DecisionAt.ToUniversalTime();
        item.RejectionReason ??= payload.Reason; // SUPPLIER_REJECTED → RejectedCount
        StatusGate.Apply(item, ItemStatuses.Rejected, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemExpiredPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, payload.SupplierId, cancellationToken);
        item.ExpiredAt ??= payload.ExpiredAt.ToUniversalTime();
        item.ExpiryReason ??= payload.Reason; // SUPPLIER_CONFIRMATION_EXPIRED → ConfirmationTimeoutCount
        StatusGate.Apply(item, ItemStatuses.Expired, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemCancelledPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, supplierId: null, cancellationToken);
        item.CancelledAt ??= payload.CancelledAt.ToUniversalTime();
        StatusGate.Apply(item, ItemStatuses.Cancelled, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemHandoverCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, payload.SupplierId, cancellationToken);
        if (item.Quantity == 0 && payload.Quantity is > 0)
        {
            item.Quantity = payload.Quantity.Value; // Created chưa tới thì lấy tạm số lượng từ handover
        }

        item.HandoverAt ??= payload.ActualHandoverAt.ToUniversalTime(); // → RentedQuantity
        StatusGate.Apply(item, ItemStatuses.InRental, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalItemReturnedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, supplierId: null, cancellationToken);
        item.ReturnedAt ??= payload.ReturnsCompletedAt.ToUniversalTime();
        StatusGate.Apply(item, ItemStatuses.PendingSettlement, payload.OrderVersion);
        return Result.Success();
    }

    /// <summary>Payload chưa có completedAt (chờ R2) nên dùng occurredAt của phong bì, theo contract mục 4.</summary>
    public async Task<Result> ApplyAsync(
        RentalItemCompletedPayload payload, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalItemFact item = await GetItemAsync(payload.RentalItemId, payload.RentalOrderId, supplierId: null, cancellationToken);
        item.CompletedAt ??= occurredAt.ToUniversalTime(); // → CompletedRentalValue, CompletedRentalCount
        StatusGate.Apply(item, ItemStatuses.Completed, payload.OrderVersion);
        return Result.Success();
    }

    private async Task<RentalItemFact> GetItemAsync(
        Guid rentalItemId, Guid rentalOrderId, Guid? supplierId, CancellationToken cancellationToken)
    {
        RentalItemFact item = await RentalFactRows.GetOrCreateItemAsync(_repository, rentalItemId, rentalOrderId, cancellationToken);
        if (item.SupplierId == Guid.Empty && supplierId is { } id && id != Guid.Empty)
        {
            item.SupplierId = id; // Dòng tạm: điền NCC sớm để báo SignalR đúng người
        }

        _changes.TrackItem(item);
        return item;
    }
}
