using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.Checkout;

/// <summary>Danh tính renter lấy từ JWT (<c>sub</c>, <c>pc</c>), không nhận từ request body.</summary>
public sealed record CheckoutRenterContext(Guid RenterId, bool IsProfileCompleted);

public sealed record CheckoutRequest(
    IReadOnlyList<Guid>? CartItemIds,
    CheckoutDeliveryDto? Delivery,
    CheckoutRefundAccountDto? RefundAccount,
    string? TermsVersion,
    bool TermsAccepted,
    decimal ExpectedTotalAmount);

public sealed record CheckoutDeliveryDto(
    string? Province,
    string? AddressDetail,
    string? RecipientName,
    string? RecipientPhone,
    string? Note);

public sealed record CheckoutRefundAccountDto(
    string? BankCode,
    string? AccountNumber,
    string? AccountHolderName);

/// <summary>
/// Kết quả validate checkout: dữ liệu đã chuẩn hóa và snapshot của từng dòng, chưa giữ lịch và chưa lưu.
/// </summary>
public sealed record CheckoutPlan(
    Guid RenterId,
    Guid CartId,
    CheckoutDeliveryDto Delivery,
    CheckoutRefundAccountDto RefundAccount,
    string TermsVersion,
    string TermsDocumentRef,
    string TermsContentHash,
    string Currency,
    IReadOnlyList<CheckoutPlanLine> Lines)
{
    public decimal TotalRentalAmount => Lines.Sum(l => l.RentalAmount);

    public decimal TotalDepositAmount => Lines.Sum(l => l.DepositAmount);

    public decimal TotalAmount => TotalRentalAmount + TotalDepositAmount;
}

public sealed record CheckoutPlanLine(
    Guid CartItemId,
    Guid EquipmentListingId,
    Guid SupplierId,
    int Quantity,
    DateOnly ExpectedStartDate,
    DateOnly ExpectedEndDate,
    string ListingName,
    string PickupLocation,
    RentalItemListingSnapshotDto ListingSnapshot,
    decimal DailyRate,
    decimal DepositPerUnit,
    int RentalDays,
    decimal RentalAmount,
    decimal DepositAmount);

public sealed record CheckoutFieldError(string Field, string Code, string Message);

public enum CheckoutLineIssueCode
{
    StartDateNotInFuture = 1,
    StartDateBeyondHorizon = 2,
    RentalPeriodTooLong = 3,
    ListingUnavailable = 4,
    OwnListing = 5,
    InsufficientAvailability = 6
}

public sealed record CheckoutLineIssue(
    Guid CartItemId,
    Guid EquipmentListingId,
    CheckoutLineIssueCode Code,
    string Message,
    int? RequestedQuantity = null,
    int? AvailableQuantity = null);
