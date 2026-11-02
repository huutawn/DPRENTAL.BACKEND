using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class ListingOrganizationPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrganizationId_RoundTripsIncludingLegacyNull(bool hasOrganization)
    {
        Guid? organizationId = hasOrganization ? Guid.NewGuid() : null;
        var now = DateTimeOffset.UtcNow;
        var listing = EquipmentListing.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Test listing", null, "Model", 1, "{}", 100m, null, null, "VND", now, 2024,
            organizationId: organizationId).Value;
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(fixture.ConnectionString).Options;
        await using (var db = new InventoryServiceDbContext(options))
        {
            db.Warehouses.Add(Warehouse.CreateDraft(listing.OwnerUserId, now));
            db.EquipmentListings.Add(listing);
            await db.SaveChangesAsync();
        }
        await using (var db = new InventoryServiceDbContext(options))
        {
            var saved = await db.EquipmentListings.AsNoTracking().SingleAsync(x => x.Id == listing.Id);
            Assert.Equal(organizationId, saved.OrganizationId);
        }
    }
}
