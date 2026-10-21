namespace Dprental.PaymentService.Common.Utils;

/// <summary>
/// Kiểm tra định dạng tiền VND: số nguyên không âm, không vượt trần 999_999_999_999 VND.
/// </summary>
public static class VndAmountValidator
{
    public const decimal MaxVndAmount = 999_999_999_999m;

    public static Result ValidateVndAmount(decimal amount, string fieldName = "Số tiền")
    {
        if (amount < 0m)
        {
            return Error.Validation("Money.NegativeAmount", $"{fieldName} không được là số âm.");
        }

        if (decimal.Truncate(amount) != amount)
        {
            return Error.Validation("Money.FractionalVndNotAllowed", $"{fieldName} theo tiền tệ VND bắt buộc là số nguyên (không có phần thập phân).");
        }

        if (amount > MaxVndAmount)
        {
            return Error.Validation("Money.AmountLimitExceeded", $"{fieldName} vượt quá giới hạn giao dịch tối đa cho phép ({MaxVndAmount:N0} VND).");
        }

        return Result.Success();
    }

    public static string FormatVndAmount(decimal amount)
    {
        if (decimal.Truncate(amount) != amount)
        {
            throw new InvalidOperationException($"Số tiền VND phải là số nguyên, nhận được: {amount}");
        }
        return decimal.ToInt64(amount).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
