using Dprental.InventoryService.Application.Policies;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Policies;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test method naming convention")]
public sealed class EquipmentListingPublishPolicyOrganizationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private static EquipmentListing CreateReadyListing(Guid? organizationId = null)
    {
        var media = Guid.NewGuid();
        return EquipmentListing.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Komatsu PC200",
            null,
            "PC200",
            1,
            "{}",
            500_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024,
            mediaIds: [media],
            thumbnailMediaId: media,
            organizationId: organizationId).Value;
    }

    [Fact]
    public void GetMissingRequirements_OrganizationNotApproved_ContainsOrganizationRequirement()
    {
        var missing = EquipmentListingPublishPolicy.GetMissingRequirements(
            CreateReadyListing(), hasValidWarehouseAddress: true, hasUnitAcceptingBookings: true, organizationApproved: false);

        Assert.Equal([EquipmentListingPublishPolicy.RequirementOrganizationApproved], missing);
    }

    [Fact]
    public void CanPublish_OrganizationNotApproved_ReturnsDistinctConflictCode()
    {
        var result = EquipmentListingPublishPolicy.CanPublish(
            CreateReadyListing(), hasValidWarehouseAddress: true, hasUnitAcceptingBookings: true, organizationApproved: false);

        Assert.True(result.IsFailure);
        Assert.Equal(EquipmentListingPublishPolicy.OrganizationNotApprovedCode, result.Error.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
    }

    [Fact]
    public void CanPublish_MissingInfoAndOrganizationNotApproved_ReportsInfoFirst()
    {
        var result = EquipmentListingPublishPolicy.CanPublish(
            CreateReadyListing(), hasValidWarehouseAddress: false, hasUnitAcceptingBookings: true, organizationApproved: false);

        Assert.True(result.IsFailure);
        Assert.Equal("WAREHOUSE_ADDRESS_REQUIRED", result.Error.Code);
    }

    [Fact]
    public void IsOrganizationApproved_CallerHasOrganization_ReturnsTrue()
    {
        Assert.True(EquipmentListingPublishPolicy.IsOrganizationApproved(CreateReadyListing(), Guid.NewGuid()));
    }

    [Fact]
    public void IsOrganizationApproved_DraftListingAndCallerWithoutOrganization_ReturnsFalse()
    {
        Assert.False(EquipmentListingPublishPolicy.IsOrganizationApproved(CreateReadyListing(), null));
    }

    [Fact]
    public void IsOrganizationApproved_ActiveListingAndCallerWithoutOrganization_ReturnsTrue()
    {
        var listing = CreateReadyListing();
        Assert.True(listing.UpdateStatus(EquipmentListingStatus.Active, hasUnitAcceptingBookings: true, FixedNow).IsSuccess);

        Assert.True(EquipmentListingPublishPolicy.IsOrganizationApproved(listing, null));
    }

    [Fact]
    public void AssignOrganization_ListingWithoutOrganization_AssignsIt()
    {
        var listing = CreateReadyListing();
        var organizationId = Guid.NewGuid();

        var result = listing.AssignOrganization(organizationId, FixedNow.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(organizationId, listing.OrganizationId);
        Assert.Equal(FixedNow.AddMinutes(5), listing.UpdatedAt);
    }

    [Fact]
    public void AssignOrganization_ListingAlreadyHasOrganization_KeepsOriginal()
    {
        var original = Guid.NewGuid();
        var listing = CreateReadyListing(original);

        var result = listing.AssignOrganization(Guid.NewGuid(), FixedNow.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(original, listing.OrganizationId);
    }

    [Fact]
    public void AssignOrganization_EmptyId_ReturnsValidationError()
    {
        var listing = CreateReadyListing();

        var result = listing.AssignOrganization(Guid.Empty, FixedNow);

        Assert.True(result.IsFailure);
        Assert.Null(listing.OrganizationId);
    }
}
