using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Events.Rental;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Chiếu event cấp đơn vào analytics_rental_orders (và analytics_rental_items khi cần).
/// Cột thời gian chỉ điền một lần (??=), Status đi qua <see cref="StatusGate"/>, dữ liệu được kiểm tra
/// hết trước khi sửa để không bao giờ ghi một nửa. Không gọi SaveChanges.
/// </summary>
public sealed class RentalOrderProjector
{
    private readonly IRentalFactRepository _repository;
    private readonly ReadModelChangeSet _changes;

    public RentalOrderProjector(IRentalFactRepository repository, ReadModelChangeSet changes)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    public async Task<Result> ApplyAsync(RentalOrderCreatedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // 1. Kiểm tra toàn bộ trước khi sửa dòng nào
        if (!ProjectionErrors.IsSupportedCurrency(payload.Currency))
        {
            return Result.Failure(ProjectionErrors.CurrencyInvalid(payload.Currency));
        }

        HashSet<Guid> seenItemIds = [];
        foreach (RentalOrderCreatedItemPayload source in payload.Items)
        {
            if (!seenItemIds.Add(source.RentalItemId))
            {
                return Result.Failure(ProjectionErrors.DuplicateItem(source.RentalItemId));
            }

            if (source.Quantity < 1)
            {
                return Result.Failure(ProjectionErrors.QuantityInvalid(source.RentalItemId));
            }

            Error? amountError = FirstAmountError(source.RentalAmount, source.DepositAmount);
            if (amountError is not null)
            {
                return Result.Failure(amountError);
            }
        }

        // 2. Dòng đơn: chỉ điền cột đang trống; Created(v1) đến muộn không kéo lùi trạng thái
        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.CreatedAt ??= payload.CreatedAt.ToUniversalTime();
        StatusGate.Apply(order, OrderStatuses.PendingPayment, payload.OrderVersion);

        // 3. Dòng item: Created là nguồn snapshot chính thức nên ghi đè snapshot.
        //    Nếu RentalOrderPaid đã đến trước, item vào thẳng trạng thái đã trả tiền.
        string itemStatus = order.PaidAt is null ? ItemStatuses.PendingPayment : ItemStatuses.PendingSupplierConfirmation;
        foreach (RentalOrderCreatedItemPayload source in payload.Items)
        {
            RentalItemFact item = await RentalFactRows.GetOrCreateItemAsync(
                _repository, source.RentalItemId, payload.RentalOrderId, cancellationToken);
            item.RentalOrderId = payload.RentalOrderId;
            item.SupplierId = source.SupplierId;
            item.EquipmentListingId = source.EquipmentListingId;
            item.EquipmentTypeId = source.EquipmentTypeId;
            item.EquipmentTypeName = source.EquipmentTypeName;
            item.BrandId = source.BrandId;
            item.BrandName = source.BrandName;
            item.Model = source.Model;
            item.Quantity = source.Quantity;
            item.RentalAmount = source.RentalAmount;
            item.DepositAmount = source.DepositAmount;
            StatusGate.Apply(item, itemStatus, payload.OrderVersion);
            _changes.TrackItem(item);
        }

        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderPaidPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.PaidAt ??= payload.PaidAt.ToUniversalTime();
        StatusGate.Apply(order, OrderStatuses.Paid, payload.OrderVersion);

        // Cả đơn được trả tiền cùng lúc → mọi item đã biết chuyển sang chờ NCC xác nhận.
        // Item chưa có dòng (Created đến sau) sẽ nhận trạng thái này khi Created tới.
        foreach (RentalItemFact item in await _repository.FindItemsByOrderAsync(payload.RentalOrderId, cancellationToken))
        {
            StatusGate.Apply(item, ItemStatuses.PendingSupplierConfirmation, payload.OrderVersion);
            _changes.TrackItem(item);
        }

        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.CompletedAt ??= payload.CompletedAt.ToUniversalTime();
        StatusGate.Apply(order, OrderStatuses.Completed, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderCancelledPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.CancelledAt ??= payload.CancelledAt.ToUniversalTime();
        StatusGate.Apply(order, OrderStatuses.Cancelled, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderExpiredPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.ExpiredAt ??= payload.ExpiredAt.ToUniversalTime();
        StatusGate.Apply(order, OrderStatuses.Expired, payload.OrderVersion);
        return Result.Success();
    }

    private static Error? FirstAmountError(params decimal[] amounts)
    {
        foreach (decimal amount in amounts)
        {
            Result<long> checkedAmount = VndAmount.ToLong(amount);
            if (checkedAmount.IsFailure)
            {
                return checkedAmount.Error;
            }
        }

        return null;
    }
}
