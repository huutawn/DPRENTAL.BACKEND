using System.Text.RegularExpressions;
using Dprental.RentalService.Application.DTOs.Checkout;

namespace Dprental.RentalService.Application.Validation;

/// <summary>
/// Kiểm tra định dạng request checkout theo bảng 12 SRS Rental. Không gọi DB hay service khác.
/// </summary>
public static partial class CheckoutRequestValidator
{
    private const string Required = "REQUIRED";
    private const string InvalidLength = "INVALID_LENGTH";
    private const string InvalidFormat = "INVALID_FORMAT";
    private const string Duplicate = "DUPLICATE";
    private const string Conflicting = "CONFLICTING";

    [GeneratedRegex(@"^(0|\+84)[0-9]{9}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[0-9]{6,20}$")]
    private static partial Regex AccountNumberRegex();

    public static IReadOnlyList<CheckoutFieldError> Validate(CheckoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CheckoutFieldError> errors = [];

        ValidateSelection(request, errors);
        ValidateDeliverySource(request, errors);
        ValidateRefundAccount(request.RefundAccount, errors);

        if (string.IsNullOrWhiteSpace(request.TermsVersion))
        {
            errors.Add(new("termsVersion", Required, "Thiếu phiên bản Điều khoản thuê."));
        }

        if (!request.TermsAccepted)
        {
            errors.Add(new("termsAccepted", Required, "Bạn cần đồng ý Điều khoản thuê."));
        }

        if (request.ExpectedTotalAmount < 0)
        {
            errors.Add(new("expectedTotalAmount", InvalidFormat, "Tổng tiền không được là số âm."));
        }

        return errors;
    }

    private static void ValidateSelection(CheckoutRequest request, List<CheckoutFieldError> errors)
    {
        bool hasCartItems = request.CartItemIds is { Count: > 0 };
        bool hasBuyNow = request.BuyNow is not null;

        if (hasCartItems && hasBuyNow)
        {
            errors.Add(new("buyNow", Conflicting, "Chỉ gửi một trong hai: cartItemIds hoặc buyNow."));
            return;
        }

        if (hasBuyNow)
        {
            ValidateBuyNow(request.BuyNow!, errors);
            return;
        }

        ValidateCartItems(request.CartItemIds, errors);
    }

    private static void ValidateBuyNow(CheckoutBuyNowDto buyNow, List<CheckoutFieldError> errors)
    {
        if (buyNow.EquipmentListingId == Guid.Empty)
        {
            errors.Add(new("buyNow.equipmentListingId", Required, "Thiếu mã thiết bị muốn thuê."));
        }

        if (buyNow.Quantity < 1)
        {
            errors.Add(new("buyNow.quantity", InvalidFormat, "Số lượng thuê phải từ 1 trở lên."));
        }

        if (buyNow.ExpectedEndDate < buyNow.ExpectedStartDate)
        {
            errors.Add(new("buyNow.expectedEndDate", InvalidFormat, "Ngày trả không được trước ngày giao."));
        }
    }

    private static void ValidateCartItems(IReadOnlyList<Guid>? cartItemIds, List<CheckoutFieldError> errors)
    {
        if (cartItemIds is null || cartItemIds.Count == 0)
        {
            errors.Add(new("cartItemIds", Required, "Cần chọn ít nhất một dòng trong giỏ thuê."));
            return;
        }

        if (cartItemIds.Contains(Guid.Empty))
        {
            errors.Add(new("cartItemIds", InvalidFormat, "Mã dòng giỏ thuê không hợp lệ."));
        }

        if (cartItemIds.Distinct().Count() != cartItemIds.Count)
        {
            errors.Add(new("cartItemIds", Duplicate, "Một dòng giỏ thuê được chọn nhiều lần."));
        }
    }

    private static void ValidateDeliverySource(CheckoutRequest request, List<CheckoutFieldError> errors)
    {
        if (request.AddressId is not { } addressId)
        {
            if (request.DeliveryNote is not null)
            {
                errors.Add(new("deliveryNote", Conflicting, "deliveryNote chỉ dùng với addressId; nhập tay thì dùng delivery.note."));
            }

            ValidateDelivery(request.Delivery, errors);
            return;
        }

        if (request.Delivery is not null)
        {
            errors.Add(new("addressId", Conflicting, "Chỉ gửi một trong hai: addressId hoặc delivery."));
            return;
        }

        if (addressId == Guid.Empty)
        {
            errors.Add(new("addressId", InvalidFormat, "Mã địa chỉ không hợp lệ."));
        }

        if (request.DeliveryNote is not null && request.DeliveryNote.Trim().Length > 500)
        {
            errors.Add(new("deliveryNote", InvalidLength, "Ghi chú tối đa 500 ký tự."));
        }
    }

    private static void ValidateDelivery(CheckoutDeliveryDto? delivery, List<CheckoutFieldError> errors)
    {
        if (delivery is null)
        {
            errors.Add(new("delivery", Required, "Thiếu thông tin nơi giao."));
            return;
        }

        ValidateAddress(delivery.Province, delivery.AddressDetail, delivery.RecipientName, delivery.RecipientPhone, "delivery.", errors);

        if (delivery.Note is not null && delivery.Note.Trim().Length > 500)
        {
            errors.Add(new("delivery.note", InvalidLength, "Ghi chú tối đa 500 ký tự."));
        }
    }

    /// <summary>
    /// Luật chung cho một địa chỉ giao: dùng cho nơi giao của checkout và sổ địa chỉ của người thuê.
    /// Tên ô trong lỗi có thêm tiền tố <c>fieldPrefix</c>, ví dụ <c>delivery.</c>; sổ địa chỉ dùng chuỗi rỗng.
    /// </summary>
    internal static void ValidateAddress(
        string? province,
        string? addressDetail,
        string? recipientName,
        string? recipientPhone,
        string fieldPrefix,
        List<CheckoutFieldError> errors)
    {
        CheckLength(province, 1, 100, $"{fieldPrefix}province", "Tỉnh, thành phố giao", errors);
        CheckLength(addressDetail, 5, 200, $"{fieldPrefix}addressDetail", "Địa chỉ chi tiết", errors);
        CheckLength(recipientName, 1, 100, $"{fieldPrefix}recipientName", "Người nhận", errors);

        if (string.IsNullOrWhiteSpace(recipientPhone))
        {
            errors.Add(new($"{fieldPrefix}recipientPhone", Required, "Thiếu số điện thoại người nhận."));
        }
        else if (!PhoneRegex().IsMatch(recipientPhone.Trim()))
        {
            errors.Add(new($"{fieldPrefix}recipientPhone", InvalidFormat, "Số điện thoại phải bắt đầu bằng 0 hoặc +84, theo sau 9 chữ số."));
        }
    }

    private static void ValidateRefundAccount(CheckoutRefundAccountDto? refundAccount, List<CheckoutFieldError> errors)
    {
        if (refundAccount is null)
        {
            errors.Add(new("refundAccount", Required, "Thiếu tài khoản nhận hoàn tiền."));
            return;
        }

        CheckLength(refundAccount.BankCode, 1, 50, "refundAccount.bankCode", "Ngân hàng nhận hoàn tiền", errors);
        CheckLength(refundAccount.AccountHolderName, 2, 100, "refundAccount.accountHolderName", "Tên chủ tài khoản", errors);

        if (string.IsNullOrWhiteSpace(refundAccount.AccountNumber))
        {
            errors.Add(new("refundAccount.accountNumber", Required, "Thiếu số tài khoản nhận hoàn tiền."));
        }
        else if (!AccountNumberRegex().IsMatch(refundAccount.AccountNumber.Trim()))
        {
            errors.Add(new("refundAccount.accountNumber", InvalidFormat, "Số tài khoản gồm từ 6 đến 20 chữ số."));
        }
    }

    private static void CheckLength(string? value, int min, int max, string field, string label, List<CheckoutFieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(field, Required, $"Thiếu {label}."));
            return;
        }

        int length = value.Trim().Length;
        if (length < min || length > max)
        {
            errors.Add(new(field, InvalidLength, $"{label} phải từ {min} đến {max} ký tự."));
        }
    }
}
