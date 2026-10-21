using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Orders;

/// <summary>
/// Migration AddRentalSubOrders trên PostgreSQL thật: đơn đã có trước migration được tạo đơn con theo nhà cung cấp.
/// </summary>
public sealed class RentalSubOrderMigrationPostgreSqlTests
{
    private const string MigrationBeforeSubOrders = "AddOrderCodeSequence";
    private const string SubOrdersMigration = "AddRentalSubOrders";

    // Chỉ khác byte cuối để PostgreSQL và .NET sắp xếp giống nhau: Bình đứng trước Cường.
    private static readonly Guid SupplierBinh = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid SupplierCuong = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    [Fact]
    public async Task Up_BackfillsOneSubOrderPerSupplierForExistingOrders_AndDownRemovesThem()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            RentalOrder order = OrderTestData.Order(
                Guid.NewGuid(),
                "DH-2026-000099",
                OrderTestData.Now,
                OrderTestData.Line(SupplierCuong, "Máy trộn bê tông 350L", "Máy trộn", "Hòa Phát", 2, 400_000m, 3_000_000m),
                OrderTestData.Line(SupplierBinh, "Máy xúc CAT 320", "Máy xúc", "Caterpillar", 1, 3_000_000m, 20_000_000m),
                OrderTestData.Line(SupplierBinh, "Máy đầm cóc Mikasa", "Máy đầm", "Mikasa", 1, 300_000m, 2_000_000m));

            await using (RentalServiceDbContext context = new(options))
            {
                // Chỉ lên tới migration đơn con rồi lùi lại: migration hoàn tiền trễ đứng sau cố ý chặn lùi.
                await context.GetService<IMigrator>().MigrateAsync(SubOrdersMigration);
                await using (var seed = new HistoricalReceiptSeedDbContext(options))
                {
                    seed.RentalOrders.Add(order);
                    await seed.SaveChangesAsync();
                }

                // Quay về trước migration: đơn và dòng còn nguyên, đơn con bị xóa.
                await context.GetService<IMigrator>().MigrateAsync(MigrationBeforeSubOrders);
            }

            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'rental' AND table_name = 'rental_sub_orders'"));
            Assert.Equal(0L, await ScalarAsync(connection,
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'rental' AND table_name = 'rental_items' AND column_name = 'RentalSubOrderId'"));
            Assert.Equal(3L, await ScalarAsync(connection, "SELECT COUNT(*) FROM rental.rental_items"));

            await using (RentalServiceDbContext context = new(options))
            {
                await context.Database.MigrateAsync();
            }

            Assert.Equal("NO", await ScalarAsync(connection,
                "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'rental' AND table_name = 'rental_items' AND column_name = 'RentalSubOrderId'"));

            await using (RentalServiceDbContext context = new(options))
            {
                List<RentalSubOrder> subOrders = await context.RentalSubOrders
                    .Include(s => s.Items)
                    .Where(s => s.RentalOrderId == order.Id)
                    .OrderBy(s => s.SubOrderCode)
                    .ToListAsync();

                Assert.Equal(["DH-2026-000099-01", "DH-2026-000099-02"], subOrders.Select(s => s.SubOrderCode));
                Assert.Equal([SupplierBinh, SupplierCuong], subOrders.Select(s => s.SupplierId));
                Assert.Equal(2, subOrders[0].Items.Count);
                Assert.All(subOrders[0].Items, i => Assert.Equal(SupplierBinh, i.SupplierId));
                Assert.Equal(SupplierCuong, Assert.Single(subOrders[1].Items).SupplierId);
                Assert.All(subOrders, s => Assert.Equal(OrderTestData.Now, s.CreatedAt));
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
