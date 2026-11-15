using System.Globalization;
using System.Text;
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

    /// <summary>Độ dài tối đa của ghi chú giao hàng, lúc checkout và khi sửa sau khi đặt.</summary>
    public const int MaxDeliveryNoteLength = 500;
    private const string Conflicting = "CONFLICTING";

    [GeneratedRegex(@"^(0|\+84)[0-9]{9}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[0-9]{6,20}$")]
    private static partial Regex AccountNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleWhitespaceRegex();

    public static IReadOnlyList<CheckoutFieldError> Validate(CheckoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CheckoutFieldError> errors = [];

        ValidateSelection(request, errors);
        ValidateDeliveryMethod(request, errors);
        ValidateDeliverySource(request, errors);
        ValidateRefundSource(request, errors);

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

    private static void ValidateDeliveryMethod(CheckoutRequest request, List<CheckoutFieldError> errors)
    {
        if (request.DeliveryMethod is not null)
        {
            if (string.IsNullOrWhiteSpace(request.DeliveryMethod) ||
                (!string.Equals(request.DeliveryMethod.Trim(), "Delivery", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(request.DeliveryMethod.Trim(), "Pickup", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new("deliveryMethod", InvalidFormat, "Phương thức nhận máy chỉ chấp nhận 'Delivery' hoặc 'Pickup'."));
            }
        }

        if (request.ReturnMethod is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ReturnMethod) ||
                (!string.Equals(request.ReturnMethod.Trim(), "Return", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(request.ReturnMethod.Trim(), "Pickup", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new("returnMethod", InvalidFormat, "Phương thức trả máy chỉ chấp nhận 'Return' hoặc 'Pickup'."));
            }
        }
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
        bool isPickup = string.Equals(request.DeliveryMethod?.Trim(), "Pickup", StringComparison.OrdinalIgnoreCase);

        if (isPickup)
        {
            if (request.Delivery is not null && request.AddressId is not null)
            {
                errors.Add(new("addressId", Conflicting, "Chỉ gửi một trong hai: addressId hoặc delivery."));
                return;
            }

            if (request.Delivery is null && request.AddressId is null)
            {
                errors.Add(new("delivery", Required, "Thiếu thông tin người nhận khi nhận tại kho."));
                return;
            }

            if (request.Delivery is not null)
            {
                ValidateRecipientName(request.Delivery.RecipientName, "delivery.recipientName", errors);
                ValidateRecipientPhone(request.Delivery.RecipientPhone, "delivery.recipientPhone", errors);

                if (request.Delivery.Note is not null && request.Delivery.Note.Trim().Length > MaxDeliveryNoteLength)
                {
                    errors.Add(new("delivery.note", InvalidLength, $"Ghi chú tối đa {MaxDeliveryNoteLength} ký tự."));
                }
            }
            else if (request.AddressId is { } addressId)
            {
                if (addressId == Guid.Empty)
                {
                    errors.Add(new("addressId", InvalidFormat, "Mã địa chỉ không hợp lệ."));
                }
            }
            return;
        }

        if (request.AddressId is not { } addrId)
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

        if (addrId == Guid.Empty)
        {
            errors.Add(new("addressId", InvalidFormat, "Mã địa chỉ không hợp lệ."));
        }

        if (request.DeliveryNote is not null && request.DeliveryNote.Trim().Length > MaxDeliveryNoteLength)
        {
            errors.Add(new("deliveryNote", InvalidLength, $"Ghi chú tối đa {MaxDeliveryNoteLength} ký tự."));
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

        if (delivery.Note is not null && delivery.Note.Trim().Length > MaxDeliveryNoteLength)
        {
            errors.Add(new("delivery.note", InvalidLength, $"Ghi chú tối đa {MaxDeliveryNoteLength} ký tự."));
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
        ValidateRecipientName(recipientName, $"{fieldPrefix}recipientName", errors);
        ValidateRecipientPhone(recipientPhone, $"{fieldPrefix}recipientPhone", errors);
    }

    private static void ValidateRefundSource(CheckoutRequest request, List<CheckoutFieldError> errors)
    {
        if (request.RefundAccountId is not { } refundAccountId)
        {
            if (request.RefundAccount is null)
            {
                errors.Add(new("refundAccount", Required, "Thiếu tài khoản nhận hoàn tiền."));
                return;
            }

            ValidateBankAccount(
                request.RefundAccount.BankCode,
                request.RefundAccount.AccountNumber,
                request.RefundAccount.AccountHolderName,
                "refundAccount.",
                errors);
            return;
        }

        if (request.RefundAccount is not null)
        {
            errors.Add(new("refundAccountId", Conflicting, "Chỉ gửi một trong hai: refundAccountId hoặc refundAccount."));
            return;
        }

        if (refundAccountId == Guid.Empty)
        {
            errors.Add(new("refundAccountId", InvalidFormat, "Mã tài khoản nhận hoàn tiền không hợp lệ."));
        }
    }

    /// <summary>
    /// Luật chung cho một tài khoản ngân hàng nhận hoàn tiền: dùng cho tài khoản nhập tay lúc checkout và sổ tài khoản của người thuê.
    /// Tên ô trong lỗi có thêm tiền tố <c>fieldPrefix</c>, ví dụ <c>refundAccount.</c>; sổ tài khoản dùng chuỗi rỗng.
    /// </summary>
    internal static void ValidateBankAccount(
        string? bankCode,
        string? accountNumber,
        string? accountHolderName,
        string fieldPrefix,
        List<CheckoutFieldError> errors)
    {
        CheckLength(bankCode, 1, 50, $"{fieldPrefix}bankCode", "Ngân hàng nhận hoàn tiền", errors);
        CheckLength(accountHolderName, 2, 100, $"{fieldPrefix}accountHolderName", "Tên chủ tài khoản", errors);

        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            errors.Add(new($"{fieldPrefix}accountNumber", Required, "Thiếu số tài khoản nhận hoàn tiền."));
        }
        else if (!AccountNumberRegex().IsMatch(accountNumber.Trim()))
        {
            errors.Add(new($"{fieldPrefix}accountNumber", InvalidFormat, "Số tài khoản gồm từ 6 đến 20 chữ số."));
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

    private static void ValidateRecipientName(string? recipientName, string fieldName, List<CheckoutFieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(recipientName))
        {
            errors.Add(new(fieldName, Required, "Thiếu tên người nhận."));
            return;
        }

        CheckLength(recipientName, 1, 100, fieldName, "Người nhận", errors);
        if (IsPlaceholderRecipientName(recipientName))
        {
            errors.Add(new(fieldName, InvalidFormat, "Vui lòng nhập tên người nhận thật, không dùng tên mẫu."));
        }
    }

    private static void ValidateRecipientPhone(string? recipientPhone, string fieldName, List<CheckoutFieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(recipientPhone))
        {
            errors.Add(new(fieldName, Required, "Thiếu số điện thoại người nhận."));
            return;
        }

        string trimmed = recipientPhone.Trim();
        if (!PhoneRegex().IsMatch(trimmed))
        {
            errors.Add(new(fieldName, InvalidFormat, "Số điện thoại phải bắt đầu bằng 0 hoặc +84, theo sau 9 chữ số."));
            return;
        }

        if (IsPlaceholderPhone(trimmed))
        {
            errors.Add(new(fieldName, InvalidFormat, "Vui lòng nhập số điện thoại thật, không dùng số mẫu."));
        }
    }

    private static bool IsPlaceholderRecipientName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string clean = RemoveDiacritics(name.Trim()).ToLowerInvariant();
        clean = MultipleWhitespaceRegex().Replace(clean, " ");

        return clean is "nguoi thue" or "nguoi nhan" or "khach hang";
    }

    private static string RemoveDiacritics(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new(normalized.Length);
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                if (c is 'đ' or 'Đ')
                {
                    sb.Append('d');
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static bool IsPlaceholderPhone(string phone)
    {
        string normalized = phone;
        if (normalized.StartsWith("+84", StringComparison.Ordinal))
        {
            normalized = "0" + normalized[3..];
        }

        return normalized == "0900000000"
            || normalized == "0000000000"
            || normalized.Distinct().Count() <= 2;
    }
}
