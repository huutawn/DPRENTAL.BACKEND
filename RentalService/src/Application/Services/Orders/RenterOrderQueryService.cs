using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Orders;

public sealed class RenterOrderQueryService(IRentalOrderRepository orders) : IRenterOrderQueryService
{
    public async Task<Result<PagedResponse<RenterOrderSummaryResponse>>> GetMyOrdersAsync(
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result<Guid> renter = OrderQueryMapper.RequirePersonalCaller(caller);
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<RenterOrderSummaryRecord> records = await orders.GetRenterOrderPageAsync(
            renter.Value,
            (normalizedPage - 1) * normalizedPageSize,
            normalizedPageSize,
            cancellationToken);

        List<RenterOrderSummaryResponse> items = records.Items
            .Select(r => new RenterOrderSummaryResponse(
                r.OrderId,
                r.OrderCode,
                r.Status,
                r.Currency,
                r.TotalRentalAmount,
                r.TotalDepositAmount,
                r.TotalRentalAmount + r.TotalDepositAmount,
                r.ItemCount,
                r.SubOrderCount,
                r.PaymentDeadline,
                r.CreatedAt))
            .ToList();

        return OrderQueryMapper.ToPage(items, normalizedPage, normalizedPageSize, records.TotalCount);
    }

    public async Task<Result<RenterOrderDetailResponse>> GetMyOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result<Guid> renter = OrderQueryMapper.RequirePersonalCaller(caller);
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        RentalOrder? order = await orders.GetRenterOrderWithSubOrdersAsync(orderId, renter.Value, cancellationToken);
        if (order is null)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        List<RenterSubOrderResponse> subOrders = order.SubOrders
            .OrderBy(s => s.SubOrderCode, StringComparer.Ordinal)
            .Select(ToSubOrder)
            .ToList();
        decimal totalRental = subOrders.Sum(s => s.TotalRentalAmount);
        decimal totalDeposit = subOrders.Sum(s => s.TotalDepositAmount);

        return new RenterOrderDetailResponse(
            order.Id,
            order.OrderCode,
            order.Status,
            order.Currency,
            order.CreatedAt,
            order.PaymentDeadline,
            order.InvoiceRef,
            order.PaymentConfirmedAt,
            OrderQueryMapper.ToDelivery(order),
            totalRental,
            totalDeposit,
            totalRental + totalDeposit,
            subOrders);
    }

    private static RenterSubOrderResponse ToSubOrder(RentalSubOrder subOrder)
    {
        List<OrderItemResponse> items = OrderQueryMapper.ToItems(subOrder.Items);
        decimal rental = items.Sum(i => i.RentalAmount);
        decimal deposit = items.Sum(i => i.DepositAmount);

        return new RenterSubOrderResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            subOrder.SupplierId,
            SubOrderStatusPolicy.Summarize(items.Select(i => i.Status).ToList()),
            rental,
            deposit,
            rental + deposit,
            items);
    }
}
