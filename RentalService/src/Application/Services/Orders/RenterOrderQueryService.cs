using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.Orders;
using Dprental.RentalService.Application.Policies;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Orders;

public sealed class RenterOrderQueryService(IRentalOrderRepository orders) : IRenterOrderQueryService
{
    private const string RequestRefundAction = "RequestRefund";

    // Cùng cách checkout ghi tài khoản hoàn tiền vào RefundDestinationRef.
    private static readonly JsonSerializerOptions RefundAccountJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<PagedResponse<RenterOrderSummaryResponse>>> GetMyOrdersAsync(
        int page,
        int pageSize,
        string? status,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result<Guid> renter = OrderQueryMapper.RequirePersonalCaller(caller);
        if (renter.IsFailure)
        {
            return renter.Error;
        }

        Result<RentalOrderStatus?> statusFilter = OrderQueryMapper.ParseStatusFilter<RentalOrderStatus>(status);
        if (statusFilter.IsFailure)
        {
            return statusFilter.Error;
        }

        (int normalizedPage, int normalizedPageSize) = OrderQueryMapper.NormalizePaging(page, pageSize);
        RecordPage<RenterOrderSummaryRecord> records = await orders.GetRenterOrderPageAsync(
            renter.Value,
            statusFilter.Value,
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

        // Query đã lọc theo người thuê; policy chặn thêm một lớp nếu query sau này bị đổi.
        Result access = RentalAuthorizationPolicy.AuthorizeOrderRead(caller, order);
        if (access.IsFailure)
        {
            return OrderQueryErrors.OrderNotFound;
        }

        HashSet<Guid> refundRequestedItemIds = order.OperationReceipts
            .Where(r => r.Action == RequestRefundAction && r.TargetItemId.HasValue)
            .Select(r => r.TargetItemId!.Value)
            .ToHashSet();

        List<RenterSubOrderResponse> subOrders = order.SubOrders
            .OrderBy(s => s.SubOrderCode, StringComparer.Ordinal)
            .Select(s => ToSubOrder(s, refundRequestedItemIds))
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
            ToPaymentState(order),
            OrderQueryMapper.ToDelivery(order),
            ToRefundAccount(order.RefundDestinationRef),
            totalRental,
            totalDeposit,
            totalRental + totalDeposit,
            subOrders);
    }

    private static RenterSubOrderResponse ToSubOrder(RentalSubOrder subOrder, HashSet<Guid> refundRequestedItemIds)
    {
        List<RenterOrderItemResponse> items = subOrder.Items
            .OrderBy(i => i.ExpectedStartDate)
            .ThenBy(i => i.Id)
            .Select(i => ToItem(i, refundRequestedItemIds.Contains(i.Id)))
            .ToList();
        decimal rental = subOrder.Items.Sum(i => i.RentalAmount);
        decimal deposit = subOrder.Items.Sum(i => i.DepositAmount);

        return new RenterSubOrderResponse(
            subOrder.Id,
            subOrder.SubOrderCode,
            subOrder.SupplierId,
            SubOrderStatusPolicy.Summarize(subOrder.Items.Select(i => i.Status).ToList()),
            rental,
            deposit,
            rental + deposit,
            items);
    }

    private static RenterOrderItemResponse ToItem(RentalItem item, bool refundRequested)
    {
        OrderItemResponse basics = OrderQueryMapper.ToItem(item);

        return new RenterOrderItemResponse(
            basics.Name,
            basics.Quantity,
            basics.StartDate,
            basics.EndDate,
            item.Id,
            item.EquipmentListingId,
            item.Status,
            new OrderListingSnapshotResponse(
                item.ListingDescriptionSnapshot,
                item.ThumbnailMediaIdSnapshot,
                item.EquipmentTypeNameSnapshot,
                item.BrandNameSnapshot,
                item.ModelSnapshot,
                ToSpecValues(item.SpecValuesSnapshot),
                item.PickupLocationSnapshot),
            item.RentalDays,
            item.DailyRateSnapshot,
            item.DepositPerUnitSnapshot,
            item.RentalAmount,
            item.DepositAmount,
            item.SupplierConfirmationDeadline,
            item.SupplierDecisionAt,
            item.Status == RentalItemStatus.Rejected ? item.SupplierDecisionReason : null,
            ToRefund(item, refundRequested),
            item.HandoverRecord is { } handover
                ? new RenterHandoverResponse(handover.ActualHandoverAt, handover.Note, handover.RenterAcknowledgedAt)
                : null,
            ToReturns(item),
            ToSettlement(item));
    }

    private static RenterPaymentState ToPaymentState(RentalOrder order)
    {
        if (order.LatePaymentRefundRequired)
        {
            return RenterPaymentState.LatePaymentRefund;
        }

        if (order.PaymentConfirmedAt.HasValue)
        {
            return RenterPaymentState.Paid;
        }

        return order.Status == RentalOrderStatus.PendingPayment
            ? RenterPaymentState.Pending
            : RenterPaymentState.Expired;
    }

    private static RenterItemRefundResponse ToRefund(RentalItem item, bool refundRequested)
    {
        if (!string.IsNullOrWhiteSpace(item.RefundRef))
        {
            return new RenterItemRefundResponse(RenterRefundState.Completed, item.RefundRef);
        }

        return new RenterItemRefundResponse(
            refundRequested ? RenterRefundState.Processing : RenterRefundState.None,
            null);
    }

    /// <summary>Mỗi lần trả kèm số máy bình thường và hư hỏng; không trả máy cụ thể nào.</summary>
    private static List<RenterReturnResponse> ToReturns(RentalItem item) => item.ReturnRecords
        .OrderBy(r => r.ActualReturnAt)
        .ThenBy(r => r.Id)
        .Select(r =>
        {
            List<RentalUnitAssignment> units = item.UnitAssignments.Where(u => u.ReturnRecordId == r.Id).ToList();
            return new RenterReturnResponse(
                r.ActualReturnAt,
                r.Note,
                units.Count(u => u.ReturnCondition == UnitCondition.Normal),
                units.Count(u => u.ReturnCondition == UnitCondition.Damaged));
        })
        .ToList();

    private static RenterSettlementResponse? ToSettlement(RentalItem item)
    {
        if (!item.ReturnsCompletedAt.HasValue && item.DeductionProposal is null)
        {
            return null;
        }

        DeductionProposal? proposal = item.DeductionProposal;
        return new RenterSettlementResponse(
            item.ReturnsCompletedAt,
            item.DeductionDeadline,
            proposal?.Status,
            proposal?.TotalProposedAmount,
            proposal?.RenterResponseDeadline,
            item.FinalDeductionAmount,
            item.FinalDeductionAmount is { } deduction ? item.DepositAmount - deduction : null,
            item.FinancialClosureConfirmedAt);
    }

    /// <summary>Thông số được lưu dạng chuỗi JSON; trả ra dạng object. Chuỗi hỏng thì bỏ qua thay vì làm lỗi cả đơn.</summary>
    private static JsonElement? ToSpecValues(string? specValuesJson)
    {
        if (string.IsNullOrWhiteSpace(specValuesJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(specValuesJson);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checkout lưu tài khoản hoàn tiền dạng JSON trong RefundDestinationRef. Đơn cũ lưu ORIGINAL_PAYMENT_SOURCE
    /// (hoàn về nguồn thanh toán) nên không có tài khoản để trả.
    /// </summary>
    private static RenterRefundAccountResponse? ToRefundAccount(string refundDestinationRef)
    {
        if (string.IsNullOrWhiteSpace(refundDestinationRef) || !refundDestinationRef.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            CheckoutRefundAccountDto? account =
                JsonSerializer.Deserialize<CheckoutRefundAccountDto>(refundDestinationRef, RefundAccountJsonOptions);
            if (account is null ||
                string.IsNullOrWhiteSpace(account.BankCode) ||
                string.IsNullOrWhiteSpace(account.AccountNumber) ||
                string.IsNullOrWhiteSpace(account.AccountHolderName))
            {
                return null;
            }

            return new RenterRefundAccountResponse(account.BankCode, account.AccountNumber, account.AccountHolderName);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
