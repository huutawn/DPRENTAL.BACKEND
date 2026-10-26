using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Domain.Repositories;

// SaveChangesAsync của IAnalyticsUnitOfWork được DbContext có sẵn đáp ứng
public sealed class AnalyticsServiceDbContext : DbContext, IAnalyticsUnitOfWork
{
    public AnalyticsServiceDbContext(DbContextOptions<AnalyticsServiceDbContext> options) : base(options)
    {
    }

    public DbSet<RentalOrderFact> RentalOrderFacts => Set<RentalOrderFact>();
    public DbSet<RentalItemFact> RentalItemFacts => Set<RentalItemFact>();
    public DbSet<FinancialSettlementFact> FinancialSettlementFacts => Set<FinancialSettlementFact>();
    public DbSet<RefundFact> RefundFacts => Set<RefundFact>();
    public DbSet<SupplierPayoutFact> SupplierPayoutFacts => Set<SupplierPayoutFact>();
    public DbSet<AnalyticsInboxMessage> AnalyticsInboxMessages => Set<AnalyticsInboxMessage>();
    public DbSet<AnalyticsDeadLetter> AnalyticsDeadLetters => Set<AnalyticsDeadLetter>();

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AnalyticsServiceDbContext).Assembly);
    }
}
