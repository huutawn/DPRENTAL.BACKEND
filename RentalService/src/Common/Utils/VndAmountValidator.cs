namespace Dprental.RentalService.Common.Utils;

/// <summary>
/// Centralized VND precision, non-negativity, and contract v1 ceiling limits validation.
/// Reusable across commercial pricing, deposit calculations, deduction proposals, and financial receipts.
/// Invariants:
/// - VND amounts must be whole numbers (no fractional cents / decimal places).
/// - Amounts must be non-negative (>= 0).
/// - Amounts must not exceed maximum transaction ceiling defined for Rental Contract v1 (999,999,999,999 VND).
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
            return Error.Validation("Money.AmountLimitExceeded", $"{fieldName} vượt quá giới hạn giao dịch tối đa cho phép của hợp đồng Rental v1 ({MaxVndAmount:N0} VND).");
        }

        return Result.Success();
    }
}
