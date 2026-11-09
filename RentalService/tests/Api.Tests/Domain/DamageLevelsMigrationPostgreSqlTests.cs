using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class DamageLevelsMigrationPostgreSqlTests
{
    private const string PreviousMigration = "20261109100000_AddCompletedAtToRentalItemAndBackfill";

    [Fact]
    public async Task Up_SeedsLevels_AndBackfillsSnapshotOfItemsCheckedOutBefore()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");

        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            var order = PostgresTestDatabaseHelper.CreateOrderWithItem(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow);
            Guid itemId = order.Items.Single().Id;

            await using (var ctx = new RentalServiceDbContext(options))
            {
                var migrator = ctx.GetService<IMigrator>();
                await migrator.MigrateAsync();
                ctx.Add(order); // Item chưa có snapshot, giống đơn tạo trước DPR-276.
                await ctx.SaveChangesAsync();

                // Gỡ rồi chạy lại migration để SQL backfill gặp đúng một item cũ.
                await migrator.MigrateAsync(PreviousMigration);
                await migrator.MigrateAsync();
            }

            await using var check = new RentalServiceDbContext(options);
            var seeded = await check.DamageLevels.AsNoTracking().OrderBy(l => l.Level).ToListAsync();
            Assert.Equal([0, 10, 30, 50, 100], seeded.Select(l => l.Percent));

            string? snapshot = await check.RentalItems.AsNoTracking()
                .Where(i => i.Id == itemId).Select(i => i.DamageLevelsSnapshot).SingleAsync();
            var levels = DamageLevelSnapshot.Deserialize(snapshot)!;
            Assert.Equal([0, 1, 2, 3, 4], levels.Select(l => l.Level));
            Assert.Equal([0, 10, 30, 50, 100], levels.Select(l => l.Percent));
            Assert.Equal(seeded.Select(l => l.Name), levels.Select(l => l.Name));
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
