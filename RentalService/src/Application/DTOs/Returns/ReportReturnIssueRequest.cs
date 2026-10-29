namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record ReportReturnIssueRequest(
    string Reason,
    string? Note = null,
    List<Guid>? MediaIds = null,
    List<Guid>? AssignmentIds = null);
