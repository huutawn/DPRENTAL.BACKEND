using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Events;

namespace Dprental.PaymentService.Domain.Entities;

public enum ManualRefundStatus
{
    PendingManualRefund = 1,
    Completed = 2
}

public sealed class ManualRefundRequirement
{
    private readonly List<IFinancialDomainEvent> _domainEvents = [];

    public Guid Id { get; private set; }
    public string RequirementRef { get; private set; } = default!;
    public Guid SettlementId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid RentalItemId { get; private set; }
    public Guid RenterId { get; private set; }
    public string PaymentRef { get; private set; } = default!;
    public string Reason { get; private set; } = default!;
    public decimal Amount { get; private set; }
    public ManualRefundStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public string? ExternalTransferReference { get; private set; }
    public Guid? CompletedBy { get; private set; }

    public RentalItemSettlementRecord? Settlement { get; private set; }

    public IReadOnlyList<IFinancialDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();

    private ManualRefundRequirement() { }

    internal static ManualRefundRequirement CreatePending(
        Guid settlementId, Guid invoiceId, Guid rentalItemId, Guid renterId,
        string paymentRef, decimal amount, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var req = new ManualRefundRequirement
        {
            Id = id,
            RequirementRef = $"MRF-{id:N}",
            SettlementId = settlementId,
            InvoiceId = invoiceId,
            RentalItemId = rentalItemId,
            RenterId = renterId,
            PaymentRef = paymentRef,
            Reason = "RENTAL_DEPOSIT_REFUND",
            Amount = amount,
            Status = ManualRefundStatus.PendingManualRefund,
            CreatedAt = now.ToUniversalTime()
        };

        req._domainEvents.Add(new ManualRefundRequiredDomainEvent(
            req.Id, req.RequirementRef, req.SettlementId, req.RentalItemId, req.Amount, req.CreatedAt));

        return req;
    }

    public Result<bool> Complete(string externalTransferReference, Guid completedBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(externalTransferReference) ||
            externalTransferReference.Trim().Length > 256 || completedBy == Guid.Empty)
            return Error.Validation("Payment.InvalidRefundConfirmation", "Mã chuyển khoản và người xác nhận phải hợp lệ.");

        var reference = externalTransferReference.Trim();
        if (Status == ManualRefundStatus.Completed)
        {
            return string.Equals(ExternalTransferReference, reference, StringComparison.Ordinal)
                ? Result.Success(false)
                : Error.Conflict("Payment.RefundAlreadyCompleted", "Yêu cầu hoàn cọc đã được xác nhận bằng mã chuyển khoản khác.");
        }

        DateTimeOffset utcNow = now.ToUniversalTime();
        Status = ManualRefundStatus.Completed;
        ExternalTransferReference = reference;
        CompletedBy = completedBy;
        CompletedAt = utcNow;
        Revision++;

        _domainEvents.Add(new ManualRefundConfirmedDomainEvent(
            Id, RequirementRef, SettlementId, RentalItemId, Amount, reference, completedBy, utcNow));

        return Result.Success(true);
    }
}
