using System.Globalization;
using System.Text.RegularExpressions;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Admin xem được mọi đơn; số tài khoản hoàn tiền vẫn bị che và mỗi lần xem chi tiết đều được ghi log.
/// </summary>
public sealed partial class AdminOrderQueryService(
    IRentalOrderRepository orders,
    ILogger<AdminOrderQueryService> logger) : IAdminOrderQueryService
{
    private const int VisibleAccountDigits = 4;

    public async Task<Result<PagedResponse<AdminOrderSummaryResponse>>> SearchOrdersAsync(
        AdminOrderSearchRequest request,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result access = RentalAuthorizationPolicy.AuthorizeAdminOrderRead(caller);
        if (access.IsFailure)
        {
            return access.Error;
        }

        Result<AdminOrderSearchCriteria> criteria = ToCriteria(request);
        if (criteria.IsFailure)
        {
            return criteria.Error;
        }

        (int page, int pageSize) = OrderQueryMapper.NormalizePaging(request.Page, request.PageSize);
        RecordPage<AdminOrderSummaryRecord> records = await orders.SearchOrdersForAdminAsync(
            criteria.Value,
            (page - 1) * pageSize,
            pageSize,
            cancellationToken);

        List<AdminOrderSummaryResponse> items = records.Items
            .Select(r => new AdminOrderSummaryResponse(
                r.OrderId,
                r.OrderCode,
                r.Status,
                r.RenterId,
                r.SupplierIds,
                r.Currency,
                r.TotalRentalAmount,
                r.TotalDepositAmount,
                r.TotalRentalAmount + r.TotalDepositAmount,
                r.ItemCount,
                r.InvoiceRef,
                r.PaidAt,
                r.CreatedAt))
            .ToList();

        return OrderQueryMapper.ToPage(items, page, pageSize, records.TotalCount);
    }

    public async Task<Result<AdminOrderDetailResponse>> GetOrderAsync(
        Guid orderId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result access = RentalAuthorizationPolicy.AuthorizeAdminOrderRead(caller);
        if (access.IsFailure)
        {
            return access.Error;
        }

        RentalOrder? order = await orders.GetOrderForAdminAsync(orderId, cancellationToken);
        if (order is null)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        // Đơn chứa dữ liệu cá nhân của người thuê: ghi lại ai đã xem.
        LogAdminViewedOrder(logger, caller.UserId, order.Id, order.OrderCode);

        HashSet<Guid> refundRequestedItemIds = OrderQueryMapper.RefundRequestedItemIds(order);
        List<AdminSubOrderResponse> subOrders = order.SubOrders
            .OrderBy(s => s.SubOrderCode, StringComparer.Ordinal)
            .Select(s => ToSubOrder(s, refundRequestedItemIds))
            .ToList();
        decimal totalRental = subOrders.Sum(s => s.TotalRentalAmount);
        decimal totalDeposit = subOrders.Sum(s => s.TotalDepositAmount);

        List<AdminStateTransitionResponse> history = order.StateTransitions
            .OrderBy(t => t.OccurredAt)
            .ThenBy(t => t.Id)
            .Select(t => new AdminStateTransitionResponse(
                t.RentalItemId,
                t.FromState,
                t.ToState,
                t.Action,
                t.ActorId,
                t.ActorRole,
                t.Reason,
                t.OccurredAt))
            .ToList();

        return new AdminOrderDetailResponse(
            order.Id,
            order.OrderCode,
            order.Status,
            order.RenterId,
            order.Currency,
            order.CreatedAt,
            order.PaymentDeadline,
            order.InvoiceRef,
            order.PaymentRef,
            order.PaymentConfirmedAt,
            OrderQueryMapper.ToPaymentState(order),
            OrderQueryMapper.ToDelivery(order),
            OrderQueryMapper.ParseRefundAccount(order.RefundDestinationRef) is { } account
                ? new AdminRefundAccountResponse(
                    account.BankCode,
                    MaskAccountNumber(account.AccountNumber),
                    account.AccountHolderName)
                : null,
            totalRental,
            totalDeposit,
            totalRental + totalDeposit,
            subOrders,
            history,
            order.Version);
    }

    private static Result<AdminOrderSearchCriteria> ToCriteria(AdminOrderSearchRequest request)
    {
        Result<RentalOrderStatus?> status = OrderQueryMapper.ParseStatusFilter<RentalOrderStatus>(request.Status);
        if (status.IsFailure)
        {
            return status.Error;
        }

        Result<RentalItemStatus?> itemStatus = OrderQueryMapper.ParseStatusFilter<RentalItemStatus>(request.ItemStatus);
        if (itemStatus.IsFailure)
        {
            return itemStatus.Error;
        }

        if (!TryParseInstant(request.CreatedFrom, out DateTimeOffset? from) ||
            !TryParseInstant(request.CreatedTo, out DateTimeOffset? to) ||
            (from.HasValue && to.HasValue && from.Value >= to.Value))
        {
            return OrderQueryErrors.InvalidDateRange;
        }

        return new AdminOrderSearchCriteria(
            NormalizeCode(request.OrderCode),
            NormalizeCode(request.SubOrderCode),
            request.RentalItemId,
            request.ListingId,
            request.AssetId,
            request.SupplierId,
            request.RenterId,
            status.Value,
            itemStatus.Value,
            from,
            to);
    }

    /// <summary>Mã đơn luôn viết hoa (DH-2026-000001); bỏ khoảng trắng và chấp nhận chữ thường.</summary>
    private static string? NormalizeCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>Rỗng là không lọc. Bắt buộc có Z hoặc ±HH:mm để không phụ thuộc múi giờ của máy chủ.</summary>
    private static bool TryParseInstant(string? value, out DateTimeOffset? instant)
    {
        instant = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string trimmed = value.Trim();
        if (!IsoInstantRegex().IsMatch(trimmed) ||
            !DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
        {
            return false;
        }

        instant = parsed.ToUniversalTime();
        return true;
    }

    private static string MaskAccountNumber(string accountNumber) =>
        accountNumber.Length <= VisibleAccountDigits
            ? new string('*', accountNumber.Length)
            : new string('*', accountNumber.Length - VisibleAccountDigits) + accountNumber[^VisibleAccountDigits..];

    private static AdminSubOrderResponse ToSubOrder(RentalSubOrder subOrder, HashSet<Guid> refundRequestedItemIds)
    {
        List<AdminOrderItemResponse> items = subOrder.Items
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.Id)
            .Select(i => ToItem(i, refundRequestedItemIds.Contains(i.Id)))
            .ToList();
        decimal rental = subOrder.Items.Sum(i => i.RentalAmount);
        decimal deposit = subOrder.Items.Sum(i => i.DepositAmount);

        return new AdminSubOrderResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            subOrder.SupplierId,
            SubOrderStatusPolicy.Summarize(subOrder.Items.Select(i => i.Status).ToList()),
            rental,
            deposit,
            rental + deposit,
            items);
    }

    private static AdminOrderItemResponse ToItem(RentalItem item, bool refundRequested)
    {
        OrderItemResponse basics = OrderQueryMapper.ToItem(item);

        return new AdminOrderItemResponse(
            basics.Name,
            basics.Quantity,
            basics.StartDate,
            basics.EndDate,
            item.Id,
            item.EquipmentListingId,
            item.SupplierId,
            item.Status,
            item.RentalDays,
            item.DailyRateSnapshot,
            item.DepositPerUnitSnapshot,
            item.RentalAmount,
            item.DepositAmount,
            item.AllocationGroupId,
            item.InventoryConfirmationRef,
            item.SupplierConfirmationDeadline,
            item.SupplierDecisionAt,
            item.SupplierDecisionBy,
            item.SupplierDecisionReason,
            item.SupplierDecisionNote,
            OrderQueryMapper.ToRefund(item, refundRequested),
            item.SettlementRef,
            OrderQueryMapper.ToHandover(item),
            OrderQueryMapper.ToReturns(item),
            OrderQueryMapper.ToSettlement(item),
            item.UnitAssignments
                .OrderBy(u => u.Id)
                .Select(u => new AdminUnitResponse(
                    u.Id,
                    u.AssetId,
                    u.AllocationId,
                    u.Status,
                    u.HandoverCondition,
                    u.ReturnCondition,
                    u.ReturnNote,
                    u.InventoryDispositionRef))
                .ToList());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} viewed rental order {OrderId} ({OrderCode})")]
    private static partial void LogAdminViewedOrder(ILogger logger, Guid? adminId, Guid orderId, string orderCode);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex IsoInstantRegex();
}
