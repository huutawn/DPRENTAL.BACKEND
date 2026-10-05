using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Application.Mapping;

/// <summary>
/// Chuyển đổi thực thể AssetAllocation sang DTO AllocationResponse.
/// Không để lộ navigation, owner, xmin hoặc transitions.
/// </summary>
public static class InventoryAllocationMapper
{
    public static AllocationResponse ToResponse(AssetAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        return new AllocationResponse(
            Id: allocation.Id,
            AssetId: allocation.AssetId,
            SourceSystem: allocation.SourceSystem,
            ExternalReference: allocation.ExternalReference,
            StartDate: allocation.RentalPeriod.StartDate,
            EndDate: allocation.RentalPeriod.EndDate,
            Status: allocation.Status.ToString(),
            HoldExpiresAt: allocation.HoldExpiresAt,
            Notes: allocation.Notes,
            CreatedAt: allocation.CreatedAt,
            UpdatedAt: allocation.UpdatedAt
        );
    }
}
