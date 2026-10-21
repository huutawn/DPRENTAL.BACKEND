using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Api.Tests.Domain;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

public sealed class RentalItemPickupLocationSnapshotMigrationPostgreSqlTests
{
    private const string TargetMigration = "20261016064208_ExpandRentalItemPickupLocationSnapshot";
    private const string PreviousMigration = "20261015060000_MigratePendingHttpReturnReceiptsToManualReconciliation";

    [Fact]
    public async Task ExpandPickupLocationSnapshot_SavesLongSnapshot_AndDownRejectsWhenLongerThan500()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);

            // 1. Migrate to TargetMigration
            await using (RentalServiceDbContext context = new(options))
            {
                await context.GetService<IMigrator>().MigrateAsync(TargetMigration);
            }

            // Verify column max length is 1024
            await using (NpgsqlConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                await using var cmd = new NpgsqlCommand("""
                    SELECT character_maximum_length FROM information_schema.columns
                    WHERE table_schema = 'rental' AND table_name = 'rental_items' AND column_name = 'PickupLocationSnapshot'
                """, connection);
                var maxLen = (int?)await cmd.ExecuteScalarAsync();
                Assert.Equal(1024, maxLen);
            }

            // 2. Tạo đơn có địa chỉ chi tiết dài 500 ký tự và snapshot tổng dài hơn 500 ký tự (ví dụ 600 ký tự)
            var longAddressDetail = new string('A', 500);
            var longSnapshot = $"{longAddressDetail}, Phường Bến Nghé, Thành phố Hồ Chí Minh (10.776900, 106.700900)";
            Assert.True(longSnapshot.Length > 500);
            Assert.True(longSnapshot.Length <= 1024);

            var supplierId = Guid.NewGuid();
            var order = OrderTestData.Order(
                Guid.NewGuid(),
                "DH-2026-000888",
                OrderTestData.Now,
                new InitialRentalItemDto(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    supplierId,
                    1,
                    OrderTestData.Start,
                    OrderTestData.ReturnDate,
                    "Camera Cinema",
                    null,
                    longSnapshot,
                    500_000m,
                    1_000_000m,
                    "grp-long-01",
                    [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())],
                    TestListingSnapshots.Default));

            await using (var seed = new HistoricalReceiptSeedDbContext(options))
            {
                seed.RentalOrders.Add(order);
                await seed.SaveChangesAsync();
            }

            // 3. Đọc lại từ database và kiểm tra snapshot được lưu nguyên vẹn
            await using (RentalServiceDbContext context = new(options))
            {
                var loadedItem = await context.RentalItems.FirstAsync(i => i.RentalOrderId == order.Id);
                Assert.Equal(longSnapshot, loadedItem.PickupLocationSnapshot);
            }

            // 4. Down migration phải bị từ chối vì có dòng dài hơn 500 ký tự
            await using (RentalServiceDbContext context = new(options))
            {
                var migrator = context.GetService<IMigrator>();
                var ex = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(PreviousMigration));
                Assert.Contains("longer than 500 characters", ex.Message);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
