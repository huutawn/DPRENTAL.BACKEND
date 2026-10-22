using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Cart;

/// <summary>
/// Chạy RentalCartRepository qua EF thật (không dùng repository giả) để kiểm tra việc xóa dòng khỏi giỏ được lưu.
/// </summary>
public sealed class RentalCartRepositoryPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Start = new(2026, 10, 12);
    private static readonly DateOnly End = new(2026, 10, 15);

    private readonly string _dbName = $"CartRepo_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<(Guid First, Guid Second)> SeedCartWithTwoItemsAsync()
    {
        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        Guid first = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Start, End, Now).Value.Id;
        Guid second = cart.AddItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, Start, End, Now).Value.Id;

        await using RentalServiceDbContext context = NewContext();
        await new RentalCartRepository(context).CreateAsync(cart);
        return (first, second);
    }

    [Fact]
    public async Task UpdateAsync_PersistsRemovedItem()
    {
        (Guid first, Guid second) = await SeedCartWithTwoItemsAsync();

        await using (RentalServiceDbContext context = NewContext())
        {
            RentalCartRepository repository = new(context);
            RentalCart cart = (await repository.GetActiveByRenterIdAsync(_renterId))!;
            Assert.True(cart.RemoveItem(first, Now.AddMinutes(1)).IsSuccess);

            Assert.True(await repository.UpdateAsync(cart));
        }

        await using RentalServiceDbContext readContext = NewContext();
        RentalCart reloaded = (await new RentalCartRepository(readContext).GetActiveByRenterIdAsync(_renterId))!;
        Assert.Equal(second, Assert.Single(reloaded.Items).Id);
        Assert.Equal(1, await readContext.RentalCartItems.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_PersistsClearedCart()
    {
        await SeedCartWithTwoItemsAsync();

        await using (RentalServiceDbContext context = NewContext())
        {
            RentalCartRepository repository = new(context);
            RentalCart cart = (await repository.GetActiveByRenterIdAsync(_renterId))!;
            Assert.True(cart.ClearItems(Now.AddMinutes(1)).IsSuccess);

            Assert.True(await repository.UpdateAsync(cart));
        }

        await using RentalServiceDbContext readContext = NewContext();
        RentalCart reloaded = (await new RentalCartRepository(readContext).GetActiveByRenterIdAsync(_renterId))!;
        Assert.Equal(RentalCartStatus.Active, reloaded.Status);
        Assert.Empty(reloaded.Items);
        Assert.Equal(0, await readContext.RentalCartItems.CountAsync());
    }

    [Fact]
    public async Task UpdateAsync_PersistsNewItemAddedToExistingCart()
    {
        await SeedCartWithTwoItemsAsync();
        Guid newListingId = Guid.NewGuid();
        Guid newItemId;

        await using (RentalServiceDbContext context = NewContext())
        {
            RentalCartRepository repository = new(context);
            RentalCart cart = (await repository.GetActiveByRenterIdAsync(_renterId))!;
            newItemId = cart.AddItem(
                Guid.NewGuid(), newListingId, Guid.NewGuid(), 1, Start.AddDays(1), End.AddDays(1), Now.AddMinutes(1)).Value.Id;

            Assert.True(await repository.UpdateAsync(cart));
        }

        await using RentalServiceDbContext readContext = NewContext();
        RentalCart reloaded = (await new RentalCartRepository(readContext).GetActiveByRenterIdAsync(_renterId))!;
        Assert.Equal(3, reloaded.Items.Count);
        Assert.Contains(reloaded.Items, item => item.Id == newItemId && item.EquipmentListingId == newListingId);
    }

    [Fact]
    public async Task UpdateAsync_WhenDuplicateMergedOnDateUpdate_PersistsMergedQuantityAndDeletesSource()
    {
        Guid listingId = Guid.NewGuid();
        Guid ownerId = Guid.NewGuid();
        DateOnly start1 = Start;
        DateOnly end1 = End;
        DateOnly start2 = Start.AddDays(7);
        DateOnly end2 = End.AddDays(7);

        RentalCart cart = RentalCart.Create(Guid.NewGuid(), _renterId, Now).Value;
        Guid firstId = cart.AddItem(Guid.NewGuid(), listingId, ownerId, 2, start1, end1, Now).Value.Id;
        Guid secondId = cart.AddItem(Guid.NewGuid(), listingId, ownerId, 3, start2, end2, Now).Value.Id;

        await using (RentalServiceDbContext context = NewContext())
        {
            RentalCartRepository repo = new(context);
            await repo.CreateAsync(cart);
        }

        // Update dates of item 1 to start2, end2 -> merged into item 2 (total 5)
        await using (RentalServiceDbContext context = NewContext())
        {
            RentalCartRepository repo = new(context);
            RentalCart loaded = (await repo.GetActiveByRenterIdAsync(_renterId))!;
            var updateResult = loaded.UpdateItemDates(firstId, ownerId, start2, end2, Now.AddMinutes(2));
            Assert.True(updateResult.IsSuccess);
            Assert.True(await repo.UpdateAsync(loaded));
        }

        // Verify in new context: only item 2 remains, quantity is 5, item 1 is deleted from DB
        await using (RentalServiceDbContext readContext = NewContext())
        {
            RentalCart reloaded = (await new RentalCartRepository(readContext).GetActiveByRenterIdAsync(_renterId))!;
            var item = Assert.Single(reloaded.Items);
            Assert.Equal(secondId, item.Id);
            Assert.Equal(5, item.Quantity);
            Assert.Equal(start2, item.ExpectedStartDate);
            Assert.Equal(end2, item.ExpectedEndDate);
            Assert.Equal(1, await readContext.RentalCartItems.CountAsync());
        }
    }
}
