using System.ComponentModel.DataAnnotations;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Application.DTOs.Settlement;

/// <summary>
/// Lệnh quyết toán do Rental gửi sau khi đã chốt khấu trừ. RefundedDepositAmount
/// trên request là số tiền PHẢI hoàn, chưa xác nhận đã chuyển khoản.
/// </summary>
public sealed record SettleRentalRequest(
    [Required] Guid RentalOrderId,
    [Required] Guid RentalItemId,
    [Required][MaxLength(256)] string InvoiceRef,
    [Required][MaxLength(256)] string PaymentRef,
    [Required] Guid SupplierId,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal DepositAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal FinalDeductionAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RefundedDepositAmount,
    [Required][StringLength(3, MinimumLength = 3)] string Currency,
    [Required][MaxLength(256)] string DecisionRef,
    [Required][MaxLength(64)] string OperationKey)
{
    public Result Validate()
    {
        if (RentalOrderId == Guid.Empty || RentalItemId == Guid.Empty || SupplierId == Guid.Empty)
        {
            return Error.Validation("Payment.InvalidId", "RentalOrderId, RentalItemId và SupplierId phải là GUID hợp lệ.");
        }

        if (string.IsNullOrWhiteSpace(InvoiceRef) || InvoiceRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(PaymentRef) || PaymentRef.Trim().Length > 256 ||
            string.IsNullOrWhiteSpace(DecisionRef) || DecisionRef.Trim().Length > 256)
        {
            return Error.Validation("Payment.InvalidReference", "Các mã đối chiếu không được rỗng và tối đa 256 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(OperationKey) || OperationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        if (string.IsNullOrWhiteSpace(Currency) || !string.Equals(Currency.Trim(), "VND", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Payment.InvalidCurrency", "Currency bắt buộc phải là 'VND'.");
        }

        var depositValidation = VndAmountValidator.ValidateVndAmount(DepositAmount, "Tiền cọc");
        if (depositValidation.IsFailure) return depositValidation.Error;

        var deductionValidation = VndAmountValidator.ValidateVndAmount(FinalDeductionAmount, "Tiền khấu trừ cuối cùng");
        if (deductionValidation.IsFailure) return deductionValidation.Error;

        var refundValidation = VndAmountValidator.ValidateVndAmount(RefundedDepositAmount, "Tiền phải hoàn cọc");
        if (refundValidation.IsFailure) return refundValidation.Error;

        if (FinalDeductionAmount > DepositAmount)
        {
            return Error.Validation("SETTLEMENT_EXCEEDS_DEPOSIT", "Tiền khấu trừ không thể vượt quá tiền cọc.");
        }

        if (RefundedDepositAmount != DepositAmount - FinalDeductionAmount)
        {
            return Error.Validation("Payment.SettlementBreakdownMismatch", "Tiền phải hoàn cọc bắt buộc bằng tiền cọc trừ tiền khấu trừ.");
        }

        return Result.Success();
    }
}

/// <summary>
/// Status thể hiện tiếp nhận lệnh hoặc đóng tài chính; chỉ RentalSettled xác nhận kết thúc.
/// </summary>
public sealed record SettleRentalResponse(
    [Required] string SettlementRef,
    [Required] string OperationKey,
    [Required] string Status,
    decimal FinalDeductionAmount,
    decimal RefundedDepositAmount,
    [Required] string Currency,
    bool IsReplay);

public static class SettlementStatus
{
    public const string PendingManualRefund = "PENDING_MANUAL_REFUND";
    public const string Completed = "COMPLETED";
}
