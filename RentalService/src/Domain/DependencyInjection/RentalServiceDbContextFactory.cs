using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.RentalService.Domain.DependencyInjection;

/// <summary>
/// Factory khởi tạo RentalServiceDbContext tại thời điểm thiết kế (Design-time) phục vụ công cụ dotnet-ef migrations.
/// </summary>
public sealed class RentalServiceDbContextFactory : IDesignTimeDbContextFactory<RentalServiceDbContext>
{
    public RentalServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RentalServiceDbContext>();
        string designConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=rental-design.invalid;Database=rental_design;Username=design;Timeout=1";
        optionsBuilder.UseNpgsql(designConnectionString);

        return new RentalServiceDbContext(optionsBuilder.Options);
    }
}
