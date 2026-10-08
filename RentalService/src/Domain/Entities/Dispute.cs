using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("disputes", Schema = "rental")]
[Index(nameof(DeductionProposalId), IsUnique = true)]
public class Dispute
{
    [Key]
    public Guid Id { get; private set; }

    public Guid DeductionProposalId { get; private set; }

    public DisputeStatus Status { get; private set; }

    public Guid OpenedById { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    [Required]
    [MaxLength(2000)]
    public string Reason { get; private set; } = string.Empty;

    public List<Guid> MediaIds { get; private set; } = [];

    [Precision(18, 2)]
    public decimal? FinalDeductionAmount { get; private set; }

    [MaxLength(2000)]
    public string? ResolutionReason { get; private set; }

    public Guid? ResolvedById { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public List<Guid> ResolutionMediaIds { get; private set; } = [];

    [ForeignKey(nameof(DeductionProposalId))]
    public DeductionProposal? DeductionProposal { get; private set; }

    private Dispute()
    {
    }

    internal Dispute(
        Guid id,
        Guid deductionProposalId,
        Guid openedById,
        DateTimeOffset openedAt,
        string reason,
        IEnumerable<Guid>? mediaIds)
    {
        Id = id;
        DeductionProposalId = deductionProposalId;
        Status = DisputeStatus.Open;
        OpenedById = openedById;
        OpenedAt = openedAt;
        Reason = reason;
        MediaIds = (mediaIds ?? []).ToList();
    }

    internal void Resolve(
        decimal finalDeductionAmount,
        string reason,
        Guid resolvedById,
        DateTimeOffset resolvedAt,
        IEnumerable<Guid>? mediaIds)
    {
        FinalDeductionAmount = finalDeductionAmount;
        ResolutionReason = reason;
        ResolvedById = resolvedById;
        ResolvedAt = resolvedAt;
        ResolutionMediaIds = (mediaIds ?? []).ToList();
        Status = DisputeStatus.Resolved;
    }
}
