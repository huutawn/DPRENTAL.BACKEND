namespace Dprental.PaymentService.Domain.Entities;

/// <summary>Prepared settlement amounts; financial calculations belong to Application.</summary>
public sealed record RentalItemSettlement(
    string OperationKey, Guid InvoiceId, Guid InvoiceItemId, Guid RentalItemId,
    Guid SupplierId, string DecisionRef, decimal RentalAmount, decimal DepositAmount,
    decimal FinalDeductionAmount, decimal DepositRefundDue, decimal PlatformFeeRate,
    decimal PlatformFeeAmount, decimal SupplierPayable);
