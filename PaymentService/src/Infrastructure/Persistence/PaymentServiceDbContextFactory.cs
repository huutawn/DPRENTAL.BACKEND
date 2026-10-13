using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.PaymentService.Infrastructure.Persistence;

public sealed class PaymentServiceDbContextFactory : IDesignTimeDbContextFactory<PaymentServiceDbContext>
{
    public PaymentServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PaymentServiceDbContext>();
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=paymentservice;Username=paymentservice;Password=postgres";
        optionsBuilder.UseNpgsql(connectionString);
        return new PaymentServiceDbContext(optionsBuilder.Options);
    }
}
