using Dprental.RentalService.Common.Enums;

namespace Dprental.RentalService.Application.DTOs.Returns;

public sealed record RecordReturnRequest(DateTimeOffset ActualReturnAt, string? Note, List<ReturnUnitRequest> Units);
