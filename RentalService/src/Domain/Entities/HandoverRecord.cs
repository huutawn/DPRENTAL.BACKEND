using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("handover_records", Schema = "rental")]
[Index(nameof(RentalItemId), IsUnique = true)]
public class HandoverRecord
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalItemId { get; private set; }

    public DateTimeOffset ActualHandoverAt { get; private set; }

    [MaxLength(2000)]
    public string? Note { get; private set; }

    public Guid SubmittedById { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    public DateTimeOffset SupplierConfirmedAt { get; private set; }

    public Guid? RenterAcknowledgedById { get; private set; }

    public DateTimeOffset? RenterAcknowledgedAt { get; private set; }

    [MaxLength(2000)]
    public string? RenterAcknowledgementNote { get; private set; }

    [ForeignKey(nameof(RentalItemId))]
    public RentalItem? RentalItem { get; private set; }

    private readonly List<RentalUnitAssignment> _assignments = [];
    public IReadOnlyCollection<RentalUnitAssignment> Assignments => _assignments.AsReadOnly();

    private HandoverRecord()
    {
    }

    internal HandoverRecord(
        Guid id,
        Guid rentalItemId,
        DateTimeOffset actualHandoverAt,
        string? note,
        Guid submittedById,
        DateTimeOffset submittedAt,
        DateTimeOffset supplierConfirmedAt)
    {
        Id = id;
        RentalItemId = rentalItemId;
        ActualHandoverAt = actualHandoverAt;
        Note = note;
        SubmittedById = submittedById;
        SubmittedAt = submittedAt;
        SupplierConfirmedAt = supplierConfirmedAt;
    }

    internal bool Acknowledge(Guid renterId, DateTimeOffset acknowledgedAt, string? note)
    {
        if (RenterAcknowledgedAt.HasValue)
        {
            return false;
        }

        RenterAcknowledgedById = renterId;
        RenterAcknowledgedAt = acknowledgedAt;
        RenterAcknowledgementNote = note;
        return true;
    }

    internal void AddAssignment(RentalUnitAssignment assignment)
    {
        _assignments.Add(assignment);
    }
}
