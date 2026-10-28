using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.RentalService.Domain.Entities;

[Table("return_requests", Schema = "rental")]
public sealed class ReturnRequest
{
    [Key]
    public Guid Id { get; private set; }
    public Guid RentalItemId { get; private set; }
    public RentalItem? RentalItem { get; private set; }
    public DateTimeOffset ActualReturnAt { get; private set; }
    [MaxLength(2000)]
    public string? Note { get; private set; }
    public Guid SubmittedById { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }

    private readonly List<ReturnRequestUnit> _units = [];
    public IReadOnlyCollection<ReturnRequestUnit> Units => _units.AsReadOnly();

    private ReturnRequest() { }

    public ReturnRequest(Guid id, Guid rentalItemId, DateTimeOffset actualReturnAt, string? note,
        Guid submittedById, DateTimeOffset submittedAt, IEnumerable<ReturnRequestUnit> units)
    {
        Id = id;
        RentalItemId = rentalItemId;
        ActualReturnAt = actualReturnAt;
        Note = note;
        SubmittedById = submittedById;
        SubmittedAt = submittedAt;
        _units.AddRange(units);
    }
}

[Table("return_request_units", Schema = "rental")]
public sealed class ReturnRequestUnit
{
    [Key]
    public Guid Id { get; private set; }
    public Guid ReturnRequestId { get; private set; }
    public ReturnRequest? ReturnRequest { get; private set; }
    public Guid RentalUnitAssignmentId { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    private readonly List<Guid> _mediaIds = [];
    public IReadOnlyList<Guid> MediaIds => _mediaIds.AsReadOnly();

    private ReturnRequestUnit() { }

    public ReturnRequestUnit(Guid id, Guid returnRequestId, Guid rentalUnitAssignmentId, IEnumerable<Guid> mediaIds)
    {
        Id = id;
        ReturnRequestId = returnRequestId;
        RentalUnitAssignmentId = rentalUnitAssignmentId;
        _mediaIds.AddRange(mediaIds);
    }

    public void SetClosedAt(DateTimeOffset closedAt) => ClosedAt = closedAt;
}
