using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_orders", Schema = "rental")]
[Index(nameof(OrderCode), IsUnique = true)]
[Index(nameof(CheckoutOperationId), IsUnique = true)]
[Index(nameof(RenterId))]
[Index(nameof(Status))]
[Index(nameof(PaymentDeadline))]
public partial class RentalOrder
{
    [Key]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(64)]
    public string OrderCode { get; private set; } = string.Empty;

    public Guid RenterId { get; private set; }

    public RentalOrderStatus Status { get; private set; }

    [Required]
    [StringLength(3, MinimumLength = 3)]
    public string Currency { get; private set; } = "VND";

    [Required]
    [MaxLength(64)]
    public string DeliveryMethod { get; private set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string RecipientName { get; private set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string RecipientPhone { get; private set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string DeliveryAddress { get; private set; } = string.Empty;

    [MaxLength(2000)]
    public string? DeliveryNote { get; private set; }

    [Required]
    [MaxLength(256)]
    public string RefundDestinationRef { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string TermsVersion { get; private set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string TermsDocumentRef { get; private set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string TermsContentHash { get; private set; } = string.Empty;

    public DateTimeOffset PaymentDeadline { get; private set; }

    [MaxLength(256)]
    public string? InvoiceRef { get; private set; }

    [MaxLength(256)]
    public string? PaymentRef { get; private set; }

    public DateTimeOffset? PaymentConfirmedAt { get; private set; }

    public bool LatePaymentRefundRequired { get; private set; }

    [Required]
    [MaxLength(64)]
    public string CheckoutOperationId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    [ConcurrencyCheck]
    public long Version { get; private set; }

    private readonly List<RentalItem> _items = [];
    public IReadOnlyCollection<RentalItem> Items => _items.AsReadOnly();

    public RentalTermsAcceptance? TermsAcceptance { get; private set; }

    private readonly List<RentalStateTransition> _stateTransitions = [];
    public IReadOnlyCollection<RentalStateTransition> StateTransitions => _stateTransitions.AsReadOnly();

    private readonly List<RentalOperationReceipt> _operationReceipts = [];
    public IReadOnlyCollection<RentalOperationReceipt> OperationReceipts => _operationReceipts.AsReadOnly();

    private RentalOrder()
    {
    }

    private Result? CheckAndGetExistingReceipt(string operationId, string action, string fingerprint)
    {
        var existing = _operationReceipts.Find(r => r.OperationId == operationId);
        if (existing is null)
        {
            return null;
        }

        if (existing.Fingerprint != fingerprint)
        {
            return RentalErrors.OperationConflict;
        }

        return existing.IsSuccess
            ? Result.Success()
            : Result.Failure(new Error(existing.ErrorCode ?? "Operation.Failed", existing.ErrorMessage ?? string.Empty, ErrorType.Conflict));
    }

    private void RecordSuccessReceipt(string operationId, string action, string fingerprint, DateTimeOffset utcNow)
    {
        _operationReceipts.Add(new RentalOperationReceipt(
            Guid.NewGuid(),
            Id,
            operationId,
            action,
            fingerprint,
            true,
            null,
            null,
            utcNow));
    }

    private static string ComputeFingerprint(string action, Guid? targetId, ActorRole actorRole, Guid? actorId, params object?[] parameters)
    {
        StringBuilder sb = new();
        sb.Append(action).Append('|')
          .Append(targetId?.ToString() ?? "none").Append('|')
          .Append((int)actorRole).Append('|')
          .Append(actorId?.ToString() ?? "none");

        foreach (var p in parameters)
        {
            sb.Append('|');
            if (p is IEnumerable<Guid> guids)
            {
                sb.Append(string.Join(",", guids));
            }
            else if (p is IEnumerable<HandoverObservationDto> handovers)
            {
                sb.Append(string.Join(";", handovers.Select(h => $"{h.AssignmentId}:{h.Condition}:{h.Note}")));
            }
            else if (p is IEnumerable<ReturnObservationDto> returns)
            {
                sb.Append(string.Join(";", returns.Select(r => $"{r.AssignmentId}:{r.Condition}:{r.Note}")));
            }
            else if (p is IEnumerable<DeductionLineDto> deductions)
            {
                sb.Append(string.Join(";", deductions.Select(d => $"{d.AssignmentId}:{d.ProposedAmount}:{d.Reason}")));
            }
            else
            {
                sb.Append(p?.ToString() ?? "null");
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private void RecalculateStatus()
    {
        if (Status is RentalOrderStatus.PendingPayment or RentalOrderStatus.Expired or RentalOrderStatus.Cancelled)
        {
            if (_items.All(i => i.Status == RentalItemStatus.PendingPayment))
            {
                Status = RentalOrderStatus.PendingPayment;
                return;
            }
            if (_items.All(i => i.Status == RentalItemStatus.Expired))
            {
                Status = RentalOrderStatus.Expired;
                return;
            }
            if (_items.All(i => i.Status == RentalItemStatus.Cancelled))
            {
                Status = RentalOrderStatus.Cancelled;
                return;
            }
        }

        if (_items.Any(i => i.Status == RentalItemStatus.InRental))
        {
            Status = RentalOrderStatus.InRental;
            return;
        }

        if (_items.Any(i => i.Status == RentalItemStatus.PendingSettlement))
        {
            Status = RentalOrderStatus.PendingSettlement;
            return;
        }

        if (_items.Any(i => i.Status == RentalItemStatus.AwaitingHandover))
        {
            Status = RentalOrderStatus.AwaitingHandover;
            return;
        }

        if (_items.Any(i => i.Status == RentalItemStatus.PendingSupplierConfirmation))
        {
            Status = RentalOrderStatus.PendingSupplierConfirmation;
            return;
        }

        bool allTerminal = _items.All(i => i.Status is RentalItemStatus.Completed
            or RentalItemStatus.Rejected
            or RentalItemStatus.Expired
            or RentalItemStatus.Cancelled);

        if (allTerminal)
        {
            if (_items.Any(i => i.Status == RentalItemStatus.Completed))
            {
                Status = RentalOrderStatus.Completed;
                return;
            }

            if (_items.All(i => i.Status == RentalItemStatus.Rejected))
            {
                Status = RentalOrderStatus.Rejected;
                return;
            }

            if (_items.All(i => i.Status == RentalItemStatus.Expired))
            {
                Status = RentalOrderStatus.Expired;
                return;
            }

            if (_items.All(i => i.Status == RentalItemStatus.Cancelled))
            {
                Status = RentalOrderStatus.Cancelled;
                return;
            }

            Status = RentalOrderStatus.Cancelled;
        }
    }

    private void LogTransition(
        Guid? rentalItemId,
        string? fromState,
        string toState,
        string action,
        Guid? actorId,
        ActorRole actorRole,
        string? reason,
        DateTimeOffset occurredAt,
        string operationId)
    {
        RentalStateTransition transition = new(
            Guid.NewGuid(),
            Id,
            rentalItemId,
            fromState,
            toState,
            action,
            actorId,
            actorRole,
            reason,
            occurredAt,
            operationId);

        _stateTransitions.Add(transition);
    }
}
