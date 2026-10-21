using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

public sealed class RentalCartRepository(RentalServiceDbContext dbContext) : IRentalCartRepository
{
    public async Task<RentalCart?> GetActiveByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        return await dbContext.RentalCarts
            .Include(cart => cart.Items)
            .FirstOrDefaultAsync(cart => cart.RenterId == renterId && cart.Status == RentalCartStatus.Active, cancellationToken);
    }

    public async Task<RentalCart?> GetByRenterIdAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        return await dbContext.RentalCarts
            .Include(cart => cart.Items)
            .OrderByDescending(cart => cart.UpdatedAt)
            .FirstOrDefaultAsync(cart => cart.RenterId == renterId, cancellationToken);
    }

    public async Task<RentalCart?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await dbContext.RentalCarts
            .Include(cart => cart.Items)
            .FirstOrDefaultAsync(cart => cart.Id == id, cancellationToken);
    }

    public async Task<RentalCart> CreateAsync(RentalCart cart, CancellationToken cancellationToken = default)
    {
        dbContext.RentalCarts.Add(cart);
        await dbContext.SaveChangesAsync(cancellationToken);
        return cart;
    }

    public async Task<bool> UpdateAsync(RentalCart cart, CancellationToken cancellationToken = default)
    {
        if (dbContext.Entry(cart).State == EntityState.Detached)
        {
            return false;
        }

        HashSet<Guid> persistedItemIds = await dbContext.RentalCartItems
            .AsNoTracking()
            .Where(item => item.RentalCartId == cart.Id)
            .Select(item => item.Id)
            .ToHashSetAsync(cancellationToken);

        foreach (RentalCartItem item in cart.Items)
        {
            if (!persistedItemIds.Contains(item.Id))
            {
                dbContext.Entry(item).State = EntityState.Added;
            }
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var cart = await dbContext.RentalCarts.FindAsync(new object[] { id }, cancellationToken);
        if (cart is null) return false;

        dbContext.RentalCarts.Remove(cart);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
