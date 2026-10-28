namespace Dprental.PaymentService.Domain.Entities;

public static class FinancialRefundKinds
{
    public const string CancellationRefund = "CancellationRefund";
    public const string LatePaymentRefund = "LatePaymentRefund";
    public const string SettlementDepositRefund = "SettlementDepositRefund";

    public static bool IsRentalRefundKind(string? value) => value is CancellationRefund or LatePaymentRefund;
}
