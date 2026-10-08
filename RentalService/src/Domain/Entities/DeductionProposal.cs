using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("deduction_proposals", Schema = "rental")]
[Index(nameof(RentalItemId), IsUnique = true)]
public class DeductionProposal
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalItemId { get; private set; }

    public DeductionProposalStatus Status { get; private set; }

    public Guid SubmittedById { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset RenterResponseDeadline { get; private set; }

    public Guid? RespondedById { get; private set; }

    public DateTimeOffset? RespondedAt { get; private set; }

    [NotMapped]
    public decimal TotalProposedAmount => _assignments.Sum(a => a.ProposedDeductionAmount ?? 0m);

    [ForeignKey(nameof(RentalItemId))]
    public RentalItem? RentalItem { get; private set; }

    private readonly List<RentalUnitAssignment> _assignments = [];
    public IReadOnlyCollection<RentalUnitAssignment> Assignments => _assignments.AsReadOnly();

    public Dispute? Dispute { get; private set; }

    private DeductionProposal()
    {
    }

    internal DeductionProposal(
        Guid id,
        Guid rentalItemId,
        Guid submittedById,
        DateTimeOffset submittedAt,
        DateTimeOffset renterResponseDeadline)
    {
        Id = id;
        RentalItemId = rentalItemId;
        Status = DeductionProposalStatus.Submitted;
        SubmittedById = submittedById;
        SubmittedAt = submittedAt;
        RenterResponseDeadline = renterResponseDeadline;
    }

    internal void AddAssignment(RentalUnitAssignment assignment)
    {
        _assignments.Add(assignment);
    }

    internal void Accept(Guid renterId, DateTimeOffset acceptedAt)
    {
        Status = DeductionProposalStatus.Accepted;
        RespondedById = renterId;
        RespondedAt = acceptedAt;
    }

    internal void AcceptByTimeout(DateTimeOffset timeoutAt)
    {
        Status = DeductionProposalStatus.AcceptedByTimeout;
        RespondedAt = timeoutAt;
    }

    internal Dispute OpenDispute(
        Guid disputeId,
        Guid renterId,
        string reason,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset openedAt)
    {
        Status = DeductionProposalStatus.Disputed;
        RespondedById = renterId;
        RespondedAt = openedAt;
        Dispute = new Dispute(disputeId, Id, renterId, openedAt, reason, mediaIds);
        return Dispute;
    }

    internal void ApplyResolution()
    {
        Status = DeductionProposalStatus.Resolved;
    }
}
