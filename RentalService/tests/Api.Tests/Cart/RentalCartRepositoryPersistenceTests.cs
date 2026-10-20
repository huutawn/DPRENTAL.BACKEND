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
}
