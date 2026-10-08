using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("return_records", Schema = "rental")]
[Index(nameof(RentalItemId))]
public class ReturnRecord
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalItemId { get; private set; }

    public DateTimeOffset ActualReturnAt { get; private set; }

    [MaxLength(2000)]
    public string? Note { get; private set; }

    public Guid SubmittedById { get; private set; }

    public DateTimeOffset SubmittedAt { get; private set; }

    [ForeignKey(nameof(RentalItemId))]
    public RentalItem? RentalItem { get; private set; }

    private readonly List<RentalUnitAssignment> _assignments = [];
    public IReadOnlyCollection<RentalUnitAssignment> Assignments => _assignments.AsReadOnly();

    private ReturnRecord()
    {
    }

    internal ReturnRecord(
        Guid id,
        Guid rentalItemId,
        DateTimeOffset actualReturnAt,
        string? note,
        Guid submittedById,
        DateTimeOffset submittedAt)
    {
        Id = id;
        RentalItemId = rentalItemId;
        ActualReturnAt = actualReturnAt;
        Note = note;
        SubmittedById = submittedById;
        SubmittedAt = submittedAt;
    }

    internal void AddAssignment(RentalUnitAssignment assignment)
    {
        _assignments.Add(assignment);
    }
}
