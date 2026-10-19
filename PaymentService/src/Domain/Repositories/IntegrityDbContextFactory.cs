using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.PaymentService.Domain.Repositories;

public sealed class IntegrityDbContextFactory : IDesignTimeDbContextFactory<IntegrityDbContext>
{
    public IntegrityDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<IntegrityDbContext>();
        string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__IntegrityPostgres")
            ?? "Host=localhost;Database=integrity_evidence;Username=integrityservice;Password=postgres";
        optionsBuilder.UseNpgsql(connectionString, b => b.MigrationsAssembly(typeof(IntegrityDbContext).Assembly.FullName));
        return new IntegrityDbContext(optionsBuilder.Options);
    }
}
