using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

/// <summary>Seeds orders against migrations predating InventoryOperationKey without changing their schema.</summary>
internal sealed class HistoricalReceiptSeedDbContext(DbContextOptions<RentalServiceDbContext> options)
    : RentalServiceDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<RentalOperationReceipt>()
            .Property(r => r.InventoryOperationKey)
            .Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
