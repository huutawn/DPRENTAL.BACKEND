using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Returns;

public record RecordReturnResponse(
    Guid ReturnRecordId,
    DateTimeOffset SubmittedAt,
    int RemainingUnits,
    RentalItemStatus Status,
    DateTimeOffset? DeductionDeadline
);  