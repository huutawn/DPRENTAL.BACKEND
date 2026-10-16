using System.Text.Json;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.DTOs.Orders;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Services.Orders;

/// <summary>
/// Phần dùng chung của API xem đơn: kiểm tra người gọi, chuẩn hóa phân trang và đổi entity sang response.
/// </summary>
internal static class OrderQueryMapper
{
    public const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const string RequestRefundAction = "RequestRefund";

    // Cùng cách checkout ghi tài khoản hoàn tiền vào RefundDestinationRef.
    private static readonly JsonSerializerOptions RefundAccountJsonOptions = new(JsonSerializerDefaults.Web);

    public static Result<Guid> RequirePersonalCaller(IRentalCallerContext caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (!caller.IsAuthenticated)
        {
            return OrderQueryErrors.Unauthenticated;
        }

        if (caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            return OrderQueryErrors.PersonalAccountRequired;
        }

        return caller.UserId.Value;
    }

    /// <summary>page nhỏ hơn 1 được đổi thành 1; pageSize được giới hạn trong 1–100, giống API lịch thuê.</summary>
    public static (int Page, int PageSize) NormalizePaging(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    /// <summary>
    /// Đọc trạng thái lọc theo đúng dạng API trả ra (PENDING_PAYMENT), chấp nhận cả PendingPayment.
    /// Rỗng là không lọc; số hoặc tên lạ là lỗi.
    /// </summary>
    public static Result<TEnum?> ParseStatusFilter<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result.Success<TEnum?>(null);
        }

        string name = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        if (!name.All(char.IsAsciiLetter) || !Enum.TryParse(name, ignoreCase: true, out TEnum status))
        {
            return OrderQueryErrors.InvalidStatus;
        }

        return Result.Success<TEnum?>(status);
    }

    public static PagedResponse<T> ToPage<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount) =>
        new(items, page, pageSize, totalCount, totalCount == 0 ? 0 : (int)Math.Ceiling((double)totalCount / pageSize));

    public static OrderDeliveryResponse ToDelivery(RentalOrder order) => new(
        order.DeliveryMethod,
        order.RecipientName,
        order.RecipientPhone,
        order.DeliveryAddress,
        order.DeliveryNote);

    public static OrderItemResponse ToItem(RentalItem item, bool isSupplier = false)
    {
        // Đơn lưu ngày trả máy (không tính); trả ra ngày dùng cuối (có tính) như giỏ hàng.
        Result<(DateOnly StartDate, DateOnly EndDate)> dates =
            RentalInventoryDateAdapter.ToInventoryDates(item.ExpectedStartDate, item.ExpectedEndDate);
        DateOnly endDate = dates.IsSuccess ? dates.Value.EndDate : item.ExpectedEndDate;

        // Khách chỉ được xem địa chỉ thật sau khi chủ xác nhận đơn (Status >= AwaitingHandover).
        // Nhà cung cấp xem được địa chỉ bất kỳ lúc nào.
        bool canSeeRealAddress = isSupplier || item.Status is Dprental.RentalService.Common.Enums.RentalItemStatus.AwaitingHandover
            or Dprental.RentalService.Common.Enums.RentalItemStatus.InRental
            or Dprental.RentalService.Common.Enums.RentalItemStatus.PendingSettlement
            or Dprental.RentalService.Common.Enums.RentalItemStatus.Completed;

        string? pickupLocation = canSeeRealAddress ? item.PickupLocationSnapshot : null;

        return new OrderItemResponse(
            item.ListingTitleSnapshot,
            item.Quantity,
            item.ExpectedStartDate,
            endDate,
            pickupLocation);
    }

    /// <summary>Các dòng theo thứ tự ngày giao rồi mã dòng, để kết quả ổn định giữa các lần gọi.</summary>
    public static List<OrderItemResponse> ToItems(IEnumerable<RentalItem> items, bool isSupplier = false) => items
        .OrderBy(i => i.ExpectedStartDate)
        .ThenBy(i => i.Id)
        .Select(i => ToItem(i, isSupplier))
        .ToList();

    public static OrderHandoverResponse? ToHandover(RentalItem item) => item.HandoverRecord is { } handover
        ? new OrderHandoverResponse(handover.ActualHandoverAt, handover.Note, handover.RenterAcknowledgedAt)
        : null;

    /// <summary>Mỗi lần trả kèm số máy bình thường và hư hỏng.</summary>
    public static List<OrderReturnResponse> ToReturns(RentalItem item) => item.ReturnRecords
        .OrderBy(r => r.ActualReturnAt)
        .ThenBy(r => r.Id)
        .Select(r =>
        {
            List<RentalUnitAssignment> units = item.UnitAssignments.Where(u => u.ReturnRecordId == r.Id).ToList();
            return new OrderReturnResponse(
                r.ActualReturnAt,
                r.Note,
                units.Count(u => u.ReturnCondition == UnitCondition.Normal),
                units.Count(u => u.ReturnCondition == UnitCondition.Damaged));
        })
        .ToList();

    /// <summary>Rỗng khi chưa trả đủ máy và chưa có đề xuất khấu trừ.</summary>
    public static OrderSettlementResponse? ToSettlement(RentalItem item)
    {
        if (!item.ReturnsCompletedAt.HasValue && item.DeductionProposal is null)
        {
            return null;
        }

        DeductionProposal? proposal = item.DeductionProposal;
        return new OrderSettlementResponse(
            item.ReturnsCompletedAt,
            item.DeductionDeadline,
            proposal?.Status,
            proposal?.TotalProposedAmount,
            proposal?.RenterResponseDeadline,
            item.FinalDeductionAmount,
            item.FinalDeductionAmount is { } deduction ? item.DepositAmount - deduction : null,
            item.FinancialClosureConfirmedAt);
    }

    /// <summary>Các dòng đã được yêu cầu PaymentService hoàn tiền (có receipt RequestRefund).</summary>
    public static HashSet<Guid> RefundRequestedItemIds(RentalOrder order) => order.OperationReceipts
        .Where(r => r.Action == RequestRefundAction && r.TargetItemId.HasValue)
        .Select(r => r.TargetItemId!.Value)
        .ToHashSet();

    /// <summary>Có RefundRef là PaymentService đã hoàn xong; chỉ có yêu cầu là đang hoàn.</summary>
    public static OrderItemRefundResponse ToRefund(RentalItem item, bool refundRequested)
    {
        if (!string.IsNullOrWhiteSpace(item.RefundRef))
        {
            return new OrderItemRefundResponse(OrderRefundState.Completed, item.RefundRef);
        }

        return new OrderItemRefundResponse(
            refundRequested ? OrderRefundState.Processing : OrderRefundState.None,
            null);
    }

    /// <summary>Trả trễ cũng có PaymentConfirmedAt nên phải xét cờ trả trễ trước.</summary>
    public static OrderPaymentState ToPaymentState(RentalOrder order)
    {
        if (order.LatePaymentRefundRequired)
        {
            return OrderPaymentState.LatePaymentRefund;
        }

        if (order.PaymentConfirmedAt.HasValue)
        {
            return OrderPaymentState.Paid;
        }

        return order.Status == RentalOrderStatus.PendingPayment
            ? OrderPaymentState.Pending
            : OrderPaymentState.Expired;
    }

    /// <summary>
    /// Checkout lưu tài khoản hoàn tiền dạng JSON trong RefundDestinationRef. Đơn cũ lưu ORIGINAL_PAYMENT_SOURCE
    /// (hoàn về nguồn thanh toán) nên không có tài khoản; JSON hỏng hoặc thiếu ô cũng trả rỗng.
    /// </summary>
    public static RenterRefundAccountResponse? ParseRefundAccount(string refundDestinationRef)
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
