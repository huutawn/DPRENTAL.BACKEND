using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

[Table("return_request_issues", Schema = "rental")]
public sealed class ReturnRequestIssue
{
    [Key]
    public Guid Id { get; private set; }

    public Guid ReturnRequestId { get; private set; }
    public ReturnRequest? ReturnRequest { get; private set; }

    public Guid ReporterId { get; private set; }
    public ActorRole ReporterRole { get; private set; }

    [MaxLength(500)]
    public string Reason { get; private set; } = string.Empty;

    [MaxLength(2000)]
    public string? Note { get; private set; }

    private readonly List<Guid> _mediaIds = [];
    public IReadOnlyList<Guid> MediaIds => _mediaIds.AsReadOnly();

    public DateTimeOffset ReportedAt { get; private set; }

    private readonly List<ReturnRequestIssueUnit> _units = [];
    public IReadOnlyCollection<ReturnRequestIssueUnit> Units => _units.AsReadOnly();

    private ReturnRequestIssue() { }

    public ReturnRequestIssue(
        Guid id,
        Guid returnRequestId,
        Guid reporterId,
        ActorRole reporterRole,
        string reason,
        string? note,
        IEnumerable<Guid>? mediaIds,
        DateTimeOffset reportedAt,
        IEnumerable<Guid>? assignmentIds)
    {
        Id = id;
        ReturnRequestId = returnRequestId;
        ReporterId = reporterId;
        ReporterRole = reporterRole;
        Reason = reason;
        Note = note;
        if (mediaIds != null)
        {
            _mediaIds.AddRange(mediaIds);
        }
        ReportedAt = reportedAt;
        if (assignmentIds != null)
        {
            foreach (var assignmentId in assignmentIds)
            {
                _units.Add(new ReturnRequestIssueUnit(id, assignmentId));
            }
        }
    }
}

[Table("return_request_issue_units", Schema = "rental")]
public sealed class ReturnRequestIssueUnit
{
    public Guid ReturnRequestIssueId { get; private set; }
    public ReturnRequestIssue? ReturnRequestIssue { get; private set; }

    public Guid RentalUnitAssignmentId { get; private set; }

    private ReturnRequestIssueUnit() { }

    public ReturnRequestIssueUnit(Guid returnRequestIssueId, Guid rentalUnitAssignmentId)
    {
        ReturnRequestIssueId = returnRequestIssueId;
        RentalUnitAssignmentId = rentalUnitAssignmentId;
    }
}
