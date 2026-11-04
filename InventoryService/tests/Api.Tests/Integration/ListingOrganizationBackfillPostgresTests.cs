using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class ListingOrganizationBackfillPostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    public ListingOrganizationBackfillPostgresTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task KeysetQueryRunsOnPostgres()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(_fixture.ConnectionString).Options;
        await using var db = new InventoryServiceDbContext(options);
        var repository = new EquipmentListingRepository(db);
        Assert.Empty(await repository.GetMissingAsync(Guid.NewGuid(), 100, CancellationToken.None));
    }
}
