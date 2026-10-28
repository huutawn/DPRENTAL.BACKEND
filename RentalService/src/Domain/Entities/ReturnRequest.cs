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
    public DateTimeOffset ConfirmationDeadlineAt { get; private set; }

    private readonly List<ReturnRequestUnit> _units = [];
    public IReadOnlyCollection<ReturnRequestUnit> Units => _units.AsReadOnly();

    private readonly List<ReturnRequestIssue> _issues = [];
    public IReadOnlyCollection<ReturnRequestIssue> Issues => _issues.AsReadOnly();

    private readonly List<ReturnRequestResolution> _resolutions = [];
    public IReadOnlyCollection<ReturnRequestResolution> Resolutions => _resolutions.AsReadOnly();

    private ReturnRequest() { }

    public ReturnRequest(Guid id, Guid rentalItemId, DateTimeOffset actualReturnAt, string? note,
        Guid submittedById, DateTimeOffset submittedAt, IEnumerable<ReturnRequestUnit> units,
        DateTimeOffset? confirmationDeadlineAt = null)
    {
        Id = id;
        RentalItemId = rentalItemId;
        ActualReturnAt = actualReturnAt;
        Note = note;
        SubmittedById = submittedById;
        SubmittedAt = submittedAt;
        ConfirmationDeadlineAt = confirmationDeadlineAt ?? submittedAt.AddHours(24);
        _units.AddRange(units);
    }

    public void AddIssue(ReturnRequestIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        _issues.Add(issue);
    }

    public void AddResolution(ReturnRequestResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        _resolutions.Add(resolution);
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
    public DateTimeOffset? RejectedAt { get; private set; }
    public DateTimeOffset? OverdueNotifiedAt { get; private set; }
    private readonly List<Guid> _mediaIds = [];
    public IReadOnlyList<Guid> MediaIds => _mediaIds.AsReadOnly();

    public bool IsActionable => !ClosedAt.HasValue && !RejectedAt.HasValue;

    private ReturnRequestUnit() { }

    public ReturnRequestUnit(Guid id, Guid returnRequestId, Guid rentalUnitAssignmentId, IEnumerable<Guid> mediaIds,
        DateTimeOffset? closedAt = null, DateTimeOffset? rejectedAt = null, DateTimeOffset? overdueNotifiedAt = null)
    {
        Id = id;
        ReturnRequestId = returnRequestId;
        RentalUnitAssignmentId = rentalUnitAssignmentId;
        _mediaIds.AddRange(mediaIds);
        ClosedAt = closedAt;
        RejectedAt = rejectedAt;
        OverdueNotifiedAt = overdueNotifiedAt;
    }

    public void SetClosedAt(DateTimeOffset closedAt) => ClosedAt = closedAt;
    public void SetRejectedAt(DateTimeOffset rejectedAt) => RejectedAt = rejectedAt;
    public void SetOverdueNotifiedAt(DateTimeOffset notifiedAt) => OverdueNotifiedAt = notifiedAt;
}
