using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Kafka.Contracts;
using Dprental.AnalyticsService.Application.Kafka.Contracts.Rental;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Enums;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Services.Projections;

/// <summary>
/// Chiếu các event cấp đơn vào rental_order_facts (và rental_item_facts khi cần).
/// Quy tắc chung: cột thời gian chỉ điền một lần (??=), Status đi qua <see cref="StatusGate"/>,
/// dữ liệu được kiểm tra hết trước khi sửa để không bao giờ ghi một nửa. Không gọi SaveChanges.
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

        // 1. Kiểm tra toàn bộ dữ liệu trước khi sửa bất kỳ dòng nào
        if (!string.Equals(payload.Currency, ProjectionErrors.SupportedCurrency, StringComparison.Ordinal))
        {
            return Result.Failure(ProjectionErrors.CurrencyInvalid(payload.Currency));
        }

        Result<long> totalRental = VndAmount.ToLong(payload.TotalRentalAmount);
        if (totalRental.IsFailure)
        {
            return Result.Failure(totalRental.Error);
        }

        Result<long> totalDeposit = VndAmount.ToLong(payload.TotalDepositAmount);
        if (totalDeposit.IsFailure)
        {
            return Result.Failure(totalDeposit.Error);
        }

        List<(RentalOrderCreatedItemPayload Source, long Rental, long Deposit)> items = [];
        HashSet<Guid> seenItemIds = [];
        foreach (RentalOrderCreatedItemPayload source in payload.Items)
        {
            if (!seenItemIds.Add(source.RentalItemId))
            {
                return Result.Failure(ProjectionErrors.DuplicateItem(AnalyticsMessageTypes.RentalOrderCreated, source.RentalItemId));
            }

            Result<long> rental = VndAmount.ToLong(source.RentalAmount);
            if (rental.IsFailure)
            {
                return Result.Failure(rental.Error);
            }

            Result<long> deposit = VndAmount.ToLong(source.DepositAmount);
            if (deposit.IsFailure)
            {
                return Result.Failure(deposit.Error);
            }

            items.Add((source, rental.Value, deposit.Value));
        }

        // 2. Dữ liệu hợp lệ → sửa dòng đơn. Chỉ điền cột đang null: nếu event khác đã đến trước
        //    (dòng tạm) thì không đè lên giá trị đã có.
        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.OrderCode ??= payload.OrderCode;
        order.RenterId ??= payload.RenterId;
        order.RentalAmount ??= totalRental.Value;
        order.DepositAmount ??= totalDeposit.Value;
        order.ItemCount ??= payload.Items.Count;
        order.SupplierCount ??= payload.Items.Select(item => item.SupplierId).Distinct().Count();
        order.CreatedAt ??= payload.CreatedAt.ToUniversalTime();
        StatusGate.Apply(order, OrderFactStatus.PendingPayment, payload.OrderVersion);

        // 3. Tạo / điền các dòng item. Nếu RentalOrderPaid đã đến trước thì item nhận luôn PaidAt của đơn.
        ItemFactStatus itemStatus = order.PaidAt is null
            ? ItemFactStatus.PendingPayment
            : ItemFactStatus.PendingSupplierConfirmation;

        foreach ((RentalOrderCreatedItemPayload source, long rental, long deposit) in items)
        {
            RentalItemFact item = await RentalFactRows.GetOrCreateItemAsync(_repository, source.RentalItemId, payload.RentalOrderId, cancellationToken);
            item.SupplierId ??= source.SupplierId;
            item.EquipmentListingId ??= source.EquipmentListingId;
            item.EquipmentTypeId ??= source.EquipmentTypeId;
            item.EquipmentTypeName ??= source.EquipmentTypeName;
            item.BrandId ??= source.BrandId;
            item.BrandName ??= source.BrandName;
            item.Model ??= source.Model;
            item.Quantity ??= source.Quantity;
            item.RentalAmount ??= rental;
            item.DepositAmount ??= deposit;
            item.StartDate ??= source.ExpectedStartDate;
            item.EndDate ??= source.ExpectedEndDate;
            item.PaidAt ??= order.PaidAt;
            StatusGate.Apply(item, itemStatus, payload.OrderVersion);
            _changes.TrackItem(item);
        }

        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderPaidPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        DateTimeOffset paidAt = payload.PaidAt.ToUniversalTime();

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.PaidAt ??= paidAt;
        StatusGate.Apply(order, OrderFactStatus.Paid, payload.OrderVersion);

        // Cả đơn được trả tiền cùng lúc → mọi item đã biết nhận cùng PaidAt.
        // Item chưa có dòng (RentalOrderCreated đến sau) sẽ nhận PaidAt khi Created tới.
        foreach (RentalItemFact item in await _repository.FindItemsByOrderAsync(payload.RentalOrderId, cancellationToken))
        {
            item.PaidAt ??= paidAt;
            StatusGate.Apply(item, ItemFactStatus.PendingSupplierConfirmation, payload.OrderVersion);
            _changes.TrackItem(item);
        }

        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.CompletedAt ??= payload.CompletedAt.ToUniversalTime();
        StatusGate.Apply(order, OrderFactStatus.Completed, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderCancelledPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.CancelledAt ??= payload.CancelledAt.ToUniversalTime();
        StatusGate.Apply(order, OrderFactStatus.Cancelled, payload.OrderVersion);
        return Result.Success();
    }

    public async Task<Result> ApplyAsync(RentalOrderExpiredPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        RentalOrderFact order = await RentalFactRows.GetOrCreateOrderAsync(_repository, payload.RentalOrderId, cancellationToken);
        order.ExpiredAt ??= payload.ExpiredAt.ToUniversalTime();
        StatusGate.Apply(order, OrderFactStatus.Expired, payload.OrderVersion);
        return Result.Success();
    }
}
