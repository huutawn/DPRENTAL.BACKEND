namespace Dprental.InventoryService.Application.DTOs.Listings;

public sealed record ConfirmListingOrganizationRequest(Guid OrganizationId);
public sealed record ConfirmListingOrganizationResponse(Guid OrganizationId);
