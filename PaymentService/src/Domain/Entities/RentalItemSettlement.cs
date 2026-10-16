using Dprental.PaymentService.Common;
using Dprental.PaymentService.Common.Utils;

namespace Dprental.PaymentService.Domain.Entities;

/// <summary>
/// Kết quả tính settlement cho một RentalItem. Thuần tính toán, không IO, không gắn EF.
/// </summary>
public sealed class RentalItemSettlement
{
    public const decimal DefaultPlatformFeeRate = 0.10m;
    public const int MaxDecisionRefLength = 256;

    /// <summary>Khóa idempotency ổn định: một RentalItem chỉ settle một lần.</summary>
    public string OperationKey { get; }

    // Tham chiếu để đối chiếu hóa đơn khi persistence.
    public Guid InvoiceId { get; }
    public Guid InvoiceItemId { get; }
    public Guid RentalItemId { get; }
    public Guid SupplierId { get; }
    public string DecisionRef { get; }

    public decimal RentalAmount { get; }
    public decimal DepositAmount { get; }
    public decimal FinalDeductionAmount { get; }

    /// <summary>Khoản cọc phải hoàn cho renter = Deposit - Deduction.</summary>
    public decimal DepositRefundDue { get; }

    /// <summary>Snapshot tỷ lệ phí tại thời điểm settle.</summary>
    public decimal PlatformFeeRate { get; }
    public decimal PlatformFeeAmount { get; }

    /// <summary>Supplier được hưởng = Rental - Fee + Deduction.</summary>
    public decimal SupplierPayable { get; }

    private RentalItemSettlement(
        string operationKey, Guid invoiceId, Guid invoiceItemId,
        Guid rentalItemId, Guid supplierId, string decisionRef,
        decimal rentalAmount, decimal depositAmount, decimal finalDeductionAmount,
        decimal depositRefundDue, decimal platformFeeRate,
        decimal platformFeeAmount, decimal supplierPayable)
    {
        OperationKey = operationKey;
        InvoiceId = invoiceId;
        InvoiceItemId = invoiceItemId;
        RentalItemId = rentalItemId;
        SupplierId = supplierId;
        DecisionRef = decisionRef;
        RentalAmount = rentalAmount;
        DepositAmount = depositAmount;
        FinalDeductionAmount = finalDeductionAmount;
        DepositRefundDue = depositRefundDue;
        PlatformFeeRate = platformFeeRate;
        PlatformFeeAmount = platformFeeAmount;
        SupplierPayable = supplierPayable;
    }

    public static Result<RentalItemSettlement> Create(
        InvoiceItem invoiceItem,
        decimal finalDeductionAmount,
        string decisionRef,
        string operationKey)
    {
        ArgumentNullException.ThrowIfNull(invoiceItem);

        if (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 64)
        {
            return Error.Validation("Payment.InvalidOperationKey", "OperationKey không được rỗng và tối đa 64 ký tự.");
        }

        var trimmedRef = decisionRef?.Trim();
        if (string.IsNullOrEmpty(trimmedRef))
        {
            return Error.Validation("Payment.InvalidDecisionRef", "DecisionRef không được để trống.");
        }

        if (trimmedRef.Length > MaxDecisionRefLength)
        {
            return Error.Validation(
                "Payment.DecisionRefTooLong",
                $"DecisionRef không được vượt quá {MaxDecisionRefLength} ký tự.");
        }

        var deductionCheck = VndAmountValidator.ValidateVndAmount(finalDeductionAmount, "Tiền khấu trừ cọc");
        if (deductionCheck.IsFailure) return deductionCheck.Error;

        var rental = invoiceItem.RentalAmount;
        var deposit = invoiceItem.DepositAmount;

        if (finalDeductionAmount > deposit)
        {
            return Error.Validation(
                "Payment.DeductionExceedsDeposit",
                "Tiền khấu trừ không được vượt quá tiền cọc.");
        }

        var refundDue = deposit - finalDeductionAmount;

        // Phí chỉ tính trên tiền thuê của chính item này, không tính trên cọc.
        var fee = Math.Round(rental * DefaultPlatformFeeRate, 0, MidpointRounding.AwayFromZero);
        var payable = rental - fee + finalDeductionAmount;

        var payableCheck = VndAmountValidator.ValidateVndAmount(payable, "Khoản supplier được hưởng");
        if (payableCheck.IsFailure) return payableCheck.Error;

        return Result.Success(new RentalItemSettlement(
            operationKey,
            invoiceItem.InvoiceId,
            invoiceItem.Id,
            invoiceItem.RentalItemId,
            invoiceItem.SupplierId,
            trimmedRef,
            rental,
            deposit,
            finalDeductionAmount,
            refundDue,
            DefaultPlatformFeeRate,
            fee,
            payable));
    }
}
