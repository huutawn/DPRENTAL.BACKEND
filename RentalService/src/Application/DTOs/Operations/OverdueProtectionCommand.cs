using Dprental.RentalService.Application.DTOs.Inventory;

namespace Dprental.RentalService.Application.DTOs.Operations;

/// <summary>Stable Inventory request, retained through local receipt retries.</summary>
public sealed record OverdueProtectionCommand(Guid AssignmentId, MarkAssetsOverdueRequest ProviderRequest);
