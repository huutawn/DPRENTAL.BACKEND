using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCompletedRequiresAuditedBackfillAndPreservesEvidence(bool supplyBackfill)
    {
        var fixture = new PostgresFixture { TargetMigration = "20261001035544_InitialInventoryBaseline" };
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<InventoryServiceDbContext>().UseNpgsql(fixture.ConnectionString).Options;
            await using var db = new InventoryServiceDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var equipment = InventoryEquipment.Create(Guid.NewGuid(), Guid.NewGuid(), CatalogLifecycleStatus.Active, true, now).Value;
            db.Equipments.Add(equipment);
            await db.SaveChangesAsync();
            var reservationId = Guid.NewGuid();
            var mediaId = Guid.NewGuid();
            var day = Dprental.InventoryService.Common.Utils.BusinessDateTime.ToVietnamDate(now);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.reservations
                (id, equipment_id, source_type, status, start_date, end_date, delivered_at, delivered_by,
                 returned_at, returned_by, return_condition, delivery_photo_ids, return_photo_ids, created_at, updated_at)
                VALUES ({reservationId}, {equipment.Id}, 1, 4, {day}, {day}, {now.AddHours(-1)}, 'staff',
                        {now}, 'staff', 'Original damage evidence', ARRAY[]::uuid[], ARRAY[{mediaId}], {now}, {now})
                """);
            if (supplyBackfill)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE inventory.return_condition_backfill (
                      reservation_id uuid PRIMARY KEY,
                      condition integer NOT NULL CHECK (condition IN (1, 2)))
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inventory.return_condition_backfill VALUES ({reservationId}, 2)");
                await db.Database.MigrateAsync();
                var migrated = await db.Reservations.SingleAsync(r => r.Id == reservationId);
                Assert.Equal(EquipmentReturnCondition.Damaged, migrated.ReturnCondition);
                Assert.Equal("Original damage evidence", migrated.ReturnConditionNotes);
                Assert.Equal(mediaId, Assert.Single(migrated.ReturnPhotoIds));
                var stagingRemoved = await db.Database.SqlQueryRaw<bool>(
                    "SELECT to_regclass('inventory.return_condition_backfill') IS NULL AS \"Value\"").SingleAsync();
                Assert.True(stagingRemoved);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
                Assert.Contains("audited", failure.Message, StringComparison.Ordinal);
                Assert.Single(await db.Database.GetAppliedMigrationsAsync());
                var original = await db.Database.SqlQuery<string>($"SELECT return_condition AS \"Value\" FROM inventory.reservations WHERE id = {reservationId}").SingleAsync();
                Assert.Equal("Original damage evidence", original);
            }
        }
        finally { await fixture.DisposeAsync(); }
    }
}
