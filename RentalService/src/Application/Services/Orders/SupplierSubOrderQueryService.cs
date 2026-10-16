using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Nhà cung cấp được định danh bằng JWT sub, giống luồng nhận/từ chối dòng thuê; chỉ thấy đơn con có SupplierId của mình.
/// </summary>
public sealed class SupplierSubOrderQueryService(IRentalOrderRepository orders) : ISupplierSubOrderQueryService
{
    public async Task<Result<PagedResponse<SupplierSubOrderSummaryResponse>>> GetMySubOrdersAsync(
        int page,
        int pageSize,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result<Guid> supplier = OrderQueryMapper.RequirePersonalCaller(caller);
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<SupplierSubOrderSummaryRecord> records = await orders.GetPaidSubOrderPageForSupplierAsync(
            supplier.Value,
            (normalizedPage - 1) * normalizedPageSize,
            normalizedPageSize,
            cancellationToken);

        List<SupplierSubOrderSummaryResponse> items = records.Items
            .Select(r => new SupplierSubOrderSummaryResponse(
                r.SubOrderId,
                r.SubOrderCode,
                r.OrderCode,
                SubOrderStatusPolicy.Summarize(r.ItemStatuses),
                r.Currency,
                r.TotalRentalAmount,
                r.TotalDepositAmount,
                r.TotalRentalAmount + r.TotalDepositAmount,
                r.ItemStatuses.Count,
                r.PaidAt,
                r.CreatedAt))
            .ToList();

        return OrderQueryMapper.ToPage(items, normalizedPage, normalizedPageSize, records.TotalCount);
    }

    public async Task<Result<SupplierSubOrderDetailResponse>> GetMySubOrderAsync(
        Guid subOrderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result<Guid> supplier = OrderQueryMapper.RequirePersonalCaller(caller);
        if (supplier.IsFailure)
        {
            return supplier.Error;
        }

        RentalSubOrder? subOrder = await orders.GetPaidSubOrderForSupplierAsync(subOrderId, supplier.Value, cancellationToken);
        if (subOrder?.RentalOrder is not RentalOrder order)
        {
            return OrderQueryErrors.SubOrderNotFound;
        }

        List<OrderItemResponse> items = OrderQueryMapper.ToItems(subOrder.Items, isSupplier: true);
        decimal rental = subOrder.Items.Sum(i => i.RentalAmount);
        decimal deposit = subOrder.Items.Sum(i => i.DepositAmount);

        return new SupplierSubOrderDetailResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            order.OrderCode,
            SubOrderStatusPolicy.Summarize(subOrder.Items.Select(i => i.Status).ToList()),
            order.Currency,
            order.PaymentConfirmedAt,
            subOrder.CreatedAt,
            OrderQueryMapper.ToDelivery(order),
            rental,
            deposit,
            rental + deposit,
            items);
    }
}
