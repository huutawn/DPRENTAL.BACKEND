using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Application.DTOs.Checkout;

/// <summary>Danh tính renter lấy từ JWT (<c>sub</c>, <c>pc</c>), không nhận từ request body.</summary>
public sealed record CheckoutRenterContext(Guid RenterId, bool IsProfileCompleted);

/// <summary>
/// DTO yêu cầu gửi đơn hàng từ giỏ thuê.
/// </summary>
/// <param name="CartItemIds">Mã các dòng giỏ muốn đặt (bắt buộc, ít nhất một dòng, không trùng nhau).</param>
/// <param name="Delivery">Nơi giao thiết bị, dùng chung cho mọi dòng trong đơn (bắt buộc).</param>
/// <param name="RefundAccount">Tài khoản nhận mọi khoản hoàn của đơn, kể cả hoàn cọc (bắt buộc).</param>
/// <param name="TermsVersion">Phiên bản Điều khoản thuê người thuê đã đọc; phải là phiên bản đang có hiệu lực.</param>
/// <param name="TermsAccepted">Người thuê đã đồng ý Điều khoản thuê (phải là true).</param>
/// <param name="ExpectedTotalAmount">Tổng tiền thuê và tiền cọc người thuê đã thấy; khác số hệ thống tính lại thì trả 409 Checkout.PriceChanged.</param>
public sealed record CheckoutRequest(
    IReadOnlyList<Guid>? CartItemIds,
    CheckoutDeliveryDto? Delivery,
    CheckoutRefundAccountDto? RefundAccount,
    string? TermsVersion,
    bool TermsAccepted,
    decimal ExpectedTotalAmount);

/// <summary>
/// Nơi giao thiết bị của đơn hàng.
/// </summary>
/// <param name="Province">Tỉnh, thành phố giao (bắt buộc, tối đa 100 ký tự).</param>
/// <param name="AddressDetail">Địa chỉ chi tiết (bắt buộc, 5 đến 200 ký tự).</param>
/// <param name="RecipientName">Tên người nhận (bắt buộc, tối đa 100 ký tự).</param>
/// <param name="RecipientPhone">Số điện thoại người nhận: bắt đầu bằng 0 hoặc +84, theo sau 9 chữ số.</param>
/// <param name="Note">Ghi chú cho nhà cung cấp (tùy chọn, tối đa 500 ký tự).</param>
public sealed record CheckoutDeliveryDto(
    string? Province,
    string? AddressDetail,
    string? RecipientName,
    string? RecipientPhone,
    string? Note);

/// <summary>
/// Tài khoản ngân hàng nhận hoàn tiền của đơn hàng.
/// </summary>
/// <param name="BankCode">Mã ngân hàng (bắt buộc, tối đa 50 ký tự).</param>
/// <param name="AccountNumber">Số tài khoản (bắt buộc, 6 đến 20 chữ số).</param>
/// <param name="AccountHolderName">Tên chủ tài khoản (bắt buộc, 2 đến 100 ký tự).</param>
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
