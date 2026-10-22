using System;
using System.Linq;
using System.Threading.Tasks;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cart;

/// <summary>
/// Kiểm tra tính bền vững và an toàn tranh chấp của RentalCart trên PostgreSQL thật:
/// migration cấu trúc bảng, khóa ngoại, unique constraints và optimistic concurrency version.
/// </summary>
public sealed class RentalCartPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start1 = new(2026, 10, 20);
    private static readonly DateOnly End1 = new(2026, 10, 23);
    private static readonly DateOnly Start2 = new(2026, 11, 1);
    private static readonly DateOnly End2 = new(2026, 11, 4);

    [Fact]
    public async Task UpdateItemDates_MergedOnRealPostgreSql_PersistsTargetAndRemovesSource()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using (RentalServiceDbContext migrate = new(options))
            {
                await migrate.Database.MigrateAsync();
            }

            Guid renterId = Guid.NewGuid();
            Guid listingId = Guid.NewGuid();
            Guid ownerId = Guid.NewGuid();
            Guid equipmentTypeId = Guid.NewGuid();

            RentalCart cart = RentalCart.Create(Guid.NewGuid(), renterId, Now).Value;
            Guid sourceItemId = cart.AddItem(Guid.NewGuid(), listingId, ownerId, equipmentTypeId, 2, Start1, End1, Now).Value.Id;
            Guid targetItemId = cart.AddItem(Guid.NewGuid(), listingId, ownerId, equipmentTypeId, 3, Start2, End2, Now).Value.Id;

            await using (RentalServiceDbContext context = new(options))
            {
                RentalCartRepository repo = new(context);
                await repo.CreateAsync(cart);
            }

            // Gộp dòng nguồn vào dòng đích trên PostgreSQL
            await using (RentalServiceDbContext context = new(options))
            {
                RentalCartRepository repo = new(context);
                RentalCart loaded = (await repo.GetActiveByRenterIdAsync(renterId))!;
                var updateResult = loaded.UpdateItemDates(sourceItemId, ownerId, Start2, End2, Now.AddMinutes(1));
                Assert.True(updateResult.IsSuccess);
                bool updated = await repo.UpdateAsync(loaded);
                Assert.True(updated);
            }

            // Kiểm tra trong context hoàn toàn mới
            await using (RentalServiceDbContext readContext = new(options))
            {
                RentalCart reloaded = (await new RentalCartRepository(readContext).GetActiveByRenterIdAsync(renterId))!;
                var item = Assert.Single(reloaded.Items);
                Assert.Equal(targetItemId, item.Id);
                Assert.Equal(5, item.Quantity);
                Assert.Equal(Start2, item.ExpectedStartDate);
                Assert.Equal(End2, item.ExpectedEndDate);

                // Dòng nguồn hoàn toàn bị xóa khỏi bảng rental_cart_items của PostgreSQL
                int totalRows = await readContext.RentalCartItems.CountAsync(i => i.RentalCartId == reloaded.Id);
                Assert.Equal(1, totalRows);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task ConcurrencyToken_DetectsConflictOnConcurrentUpdate()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using (RentalServiceDbContext migrate = new(options))
            {
                await migrate.Database.MigrateAsync();
            }

            Guid renterId = Guid.NewGuid();
            Guid listingId = Guid.NewGuid();
            Guid ownerId = Guid.NewGuid();
            Guid equipmentTypeId = Guid.NewGuid();

            RentalCart cart = RentalCart.Create(Guid.NewGuid(), renterId, Now).Value;
            Guid itemId = cart.AddItem(Guid.NewGuid(), listingId, ownerId, equipmentTypeId, 2, Start1, End1, Now).Value.Id;

            await using (RentalServiceDbContext context = new(options))
            {
                RentalCartRepository repo = new(context);
                await repo.CreateAsync(cart);
            }

            // Đọc cart từ 2 context/request khác nhau (giả lập 2 request đồng thời)
            await using RentalServiceDbContext c1 = new(options);
            RentalCartRepository repo1 = new(c1);
            RentalCart cart1 = (await repo1.GetActiveByRenterIdAsync(renterId))!;

            await using RentalServiceDbContext c2 = new(options);
            RentalCartRepository repo2 = new(c2);
            RentalCart cart2 = (await repo2.GetActiveByRenterIdAsync(renterId))!;

            // Request 1 sửa và lưu thành công (Version tăng lên)
            cart1.UpdateItemQuantity(itemId, ownerId, 4, Now.AddMinutes(1));
            bool saved1 = await repo1.UpdateAsync(cart1);
            Assert.True(saved1);

            // Request 2 cố lưu dữ liệu trên Version cũ -> DbUpdateConcurrencyException -> trả về false
            cart2.UpdateItemQuantity(itemId, ownerId, 6, Now.AddMinutes(2));
            bool saved2 = await repo2.UpdateAsync(cart2);
            Assert.False(saved2);

            // Dữ liệu cuối cùng phải phản ánh bản ghi thành công của Request 1
            await using RentalServiceDbContext readContext = new(options);
            RentalCart finalCart = (await new RentalCartRepository(readContext).GetActiveByRenterIdAsync(renterId))!;
            Assert.Equal(4, finalCart.Items.First().Quantity);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
