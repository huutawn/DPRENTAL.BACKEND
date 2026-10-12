using System.Text.RegularExpressions;

namespace Dprental.RentalService.Application.Checkout;

/// <summary>
/// Kiểm tra định dạng request checkout theo bảng 12 SRS Rental. Không gọi DB hay service khác.
/// </summary>
public static partial class CheckoutRequestValidator
{
    private const string Required = "REQUIRED";
    private const string InvalidLength = "INVALID_LENGTH";
    private const string InvalidFormat = "INVALID_FORMAT";
    private const string Duplicate = "DUPLICATE";

    [GeneratedRegex(@"^(0|\+84)[0-9]{9}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[0-9]{6,20}$")]
    private static partial Regex AccountNumberRegex();

    public static IReadOnlyList<CheckoutFieldError> Validate(CheckoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CheckoutFieldError> errors = [];

        ValidateCartItems(request.CartItemIds, errors);
        ValidateDelivery(request.Delivery, errors);
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

    private static void ValidateDelivery(CheckoutDeliveryDto? delivery, List<CheckoutFieldError> errors)
    {
        if (delivery is null)
        {
            errors.Add(new("delivery", Required, "Thiếu thông tin nơi giao."));
            return;
        }

        CheckLength(delivery.Province, 1, 100, "delivery.province", "Tỉnh, thành phố giao", errors);
        CheckLength(delivery.AddressDetail, 5, 200, "delivery.addressDetail", "Địa chỉ chi tiết", errors);
        CheckLength(delivery.RecipientName, 1, 100, "delivery.recipientName", "Người nhận", errors);

        if (string.IsNullOrWhiteSpace(delivery.RecipientPhone))
        {
            errors.Add(new("delivery.recipientPhone", Required, "Thiếu số điện thoại người nhận."));
        }
        else if (!PhoneRegex().IsMatch(delivery.RecipientPhone.Trim()))
        {
            errors.Add(new("delivery.recipientPhone", InvalidFormat, "Số điện thoại phải bắt đầu bằng 0 hoặc +84, theo sau 9 chữ số."));
        }

        if (delivery.Note is not null && delivery.Note.Trim().Length > 500)
        {
            errors.Add(new("delivery.note", InvalidLength, "Ghi chú tối đa 500 ký tự."));
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
