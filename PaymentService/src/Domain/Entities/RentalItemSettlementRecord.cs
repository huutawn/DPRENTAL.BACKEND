using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Events;

namespace Dprental.PaymentService.Domain.Entities;

public sealed class RentalItemSettlementRecord
{
    private readonly List<IFinancialDomainEvent> _domainEvents = [];

    public Guid Id { get; private set; }
    public string SettlementRef { get; private set; } = default!;
    public string OperationKey { get; private set; } = default!;
    public Guid InvoiceId { get; private set; }
    public Guid InvoiceItemId { get; private set; }
    public Guid RentalItemId { get; private set; }
    public Guid SupplierId { get; private set; }
    public string DecisionRef { get; private set; } = default!;
    public decimal RentalAmount { get; private set; }
    public decimal DepositAmount { get; private set; }
    public decimal FinalDeductionAmount { get; private set; }
    public decimal DepositRefundDue { get; private set; }
    public decimal PlatformFeeRate { get; private set; }
    public decimal PlatformFeeAmount { get; private set; }
    public decimal SupplierPayable { get; private set; }
    public SettlementState Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public List<SettlementLedgerEntry> Entries { get; private set; } = [];
    public ManualRefundRequirement? RefundRequirement { get; private set; }

    public IReadOnlyList<IFinancialDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();

    private RentalItemSettlementRecord() { }

    public static RentalItemSettlementRecord From(RentalItemSettlement s, Invoice invoice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (string.IsNullOrWhiteSpace(invoice.PaymentRef))
            throw new InvalidOperationException("Paid invoice must have a payment reference.");

        var id = Guid.NewGuid();
        var record = new RentalItemSettlementRecord
        {
            Id = id,
            SettlementRef = $"STL-{id:N}",
            OperationKey = s.OperationKey,
            InvoiceId = s.InvoiceId,
            InvoiceItemId = s.InvoiceItemId,
            RentalItemId = s.RentalItemId,
            SupplierId = s.SupplierId,
            DecisionRef = s.DecisionRef,
            RentalAmount = s.RentalAmount,
            DepositAmount = s.DepositAmount,
            FinalDeductionAmount = s.FinalDeductionAmount,
            DepositRefundDue = s.DepositRefundDue,
            PlatformFeeRate = s.PlatformFeeRate,
            PlatformFeeAmount = s.PlatformFeeAmount,
            SupplierPayable = s.SupplierPayable,
            CreatedAt = now.ToUniversalTime()
        };

        record.Entries.Add(SettlementLedgerEntry.Create(id, SettlementEntryType.SupplierPayable, s.SupplierPayable, now));
        record.Entries.Add(SettlementLedgerEntry.Create(id, SettlementEntryType.PlatformFee, s.PlatformFeeAmount, now));
        record.Entries.Add(SettlementLedgerEntry.Create(id, SettlementEntryType.DepositRefundDue, s.DepositRefundDue, now));

        if (s.DepositRefundDue > 0)
        {
            record.Status = SettlementState.PendingManualRefund;
            record.RefundRequirement = ManualRefundRequirement.CreatePending(
                id, s.InvoiceId, s.RentalItemId, invoice.RenterId,
                invoice.PaymentRef, s.DepositRefundDue, now);
        }
        else
        {
            record.Status = SettlementState.Completed;
            record.CompletedAt = now.ToUniversalTime();
        }

        record._domainEvents.Add(new RentalItemSettledDomainEvent(
            record.Id,
            record.SettlementRef,
            record.OperationKey,
            record.InvoiceId,
            record.InvoiceItemId,
            record.RentalItemId,
            record.SupplierId,
            record.RentalAmount,
            record.DepositAmount,
            record.FinalDeductionAmount,
            record.DepositRefundDue,
            record.PlatformFeeAmount,
            record.SupplierPayable,
            (int)record.Status,
            record.CreatedAt));

        return record;
    }

    public Result CompleteAfterRefund(ManualRefundRequirement refund, DateTimeOffset now)
    {
        if (refund.SettlementId != Id || DepositRefundDue <= 0 ||
            refund.Status != ManualRefundStatus.Completed)
            return Error.Conflict("Payment.RefundNotCompleted", "Chưa xác nhận hoàn đủ tiền cọc cho settlement này.");

        if (Status == SettlementState.Completed) return Result.Success();

        Status = SettlementState.Completed;
        CompletedAt = now.ToUniversalTime();

        _domainEvents.Add(new RentalItemSettledDomainEvent(
            Id,
            SettlementRef,
            OperationKey,
            InvoiceId,
            InvoiceItemId,
            RentalItemId,
            SupplierId,
            RentalAmount,
            DepositAmount,
            FinalDeductionAmount,
            DepositRefundDue,
            PlatformFeeAmount,
            SupplierPayable,
            (int)Status,
            CompletedAt.Value));

        return Result.Success();
    }
}
