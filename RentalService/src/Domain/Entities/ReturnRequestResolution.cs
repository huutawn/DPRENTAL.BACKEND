using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Domain.Entities;

[Table("return_request_resolutions", Schema = "rental")]
public sealed class ReturnRequestResolution
{
    [Key]
    public Guid Id { get; private set; }

    public Guid ReturnRequestId { get; private set; }
    public ReturnRequest? ReturnRequest { get; private set; }

    public Guid AdminId { get; private set; }
    public ReturnResolutionDecision Decision { get; private set; }

    [MaxLength(2000)]
    public string DecisionNote { get; private set; } = string.Empty;

    private readonly List<Guid> _resolutionMediaIds = [];
    public IReadOnlyList<Guid> ResolutionMediaIds => _resolutionMediaIds.AsReadOnly();

    public DateTimeOffset ResolvedAt { get; private set; }

    private readonly List<ReturnRequestResolutionUnit> _units = [];
    public IReadOnlyCollection<ReturnRequestResolutionUnit> Units => _units.AsReadOnly();

    private ReturnRequestResolution() { }

    public ReturnRequestResolution(
        Guid id,
        Guid returnRequestId,
        Guid adminId,
        ReturnResolutionDecision decision,
        string decisionNote,
        IEnumerable<Guid>? resolutionMediaIds,
        DateTimeOffset resolvedAt,
        IEnumerable<Guid>? assignmentIds)
    {
        Id = id;
        ReturnRequestId = returnRequestId;
        AdminId = adminId;
        Decision = decision;
        DecisionNote = decisionNote;
        if (resolutionMediaIds != null)
        {
            _resolutionMediaIds.AddRange(resolutionMediaIds);
        }
        ResolvedAt = resolvedAt;
        if (assignmentIds != null)
        {
            foreach (var assignmentId in assignmentIds)
            {
                _units.Add(new ReturnRequestResolutionUnit(id, assignmentId));
            }
        }
    }
}

[Table("return_request_resolution_units", Schema = "rental")]
public sealed class ReturnRequestResolutionUnit
{
    public Guid ReturnRequestResolutionId { get; private set; }
    public ReturnRequestResolution? ReturnRequestResolution { get; private set; }

    public Guid RentalUnitAssignmentId { get; private set; }

    private ReturnRequestResolutionUnit() { }

    public ReturnRequestResolutionUnit(Guid returnRequestResolutionId, Guid rentalUnitAssignmentId)
    {
        ReturnRequestResolutionId = returnRequestResolutionId;
        RentalUnitAssignmentId = rentalUnitAssignmentId;
    }
}
