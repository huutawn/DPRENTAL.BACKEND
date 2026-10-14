using Dprental.RentalService.Application.Background;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.IServices.Expiry;
using Dprental.RentalService.Application.IServices.SupplierStatistics;
using Dprental.RentalService.Application.Services.Expiry;
using Dprental.RentalService.Application.Services.SupplierStatistics;
using Dprental.RentalService.Domain.Repositories;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Expiry;

public sealed class ExpiryScannerDiAndHostIntegrationTests
{
    [Fact]
    public async Task Host_ResolvesBothExpiryAndStatistics_AndScannerExecutesWithRealServices()
    {
        string dbName = $"expiry_di_{Guid.NewGuid():N}";

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<DbContextOptions<RentalServiceDbContext>>();
                    services.RemoveAll<IDbContextOptionsConfiguration<RentalServiceDbContext>>();
                    services.AddDbContext<RentalServiceDbContext>(options =>
                        options.UseInMemoryDatabase(dbName));

                    services.RemoveAll<IOrderLockManager>();
                    services.AddSingleton<IOrderLockManager, FakeOrderLockManager>();
                });
            });

        // 1. Resolve both interfaces in one scope
        using (var scope = factory.Services.CreateScope())
        {
            var expiryService = scope.ServiceProvider.GetRequiredService<IExpiryWorkflowService>();
            var statisticsService = scope.ServiceProvider.GetRequiredService<ISupplierStatisticsService>();

            Assert.NotNull(expiryService);
            Assert.NotNull(statisticsService);
            Assert.IsType<ExpiryWorkflowService>(expiryService);
            Assert.IsType<SupplierStatisticsService>(statisticsService);
        }

        // 2. Instantiate ExpiryScannerWorker using the host's real IServiceScopeFactory
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var scanner = new ExpiryScannerWorker(scopeFactory, configuration, NullLogger<ExpiryScannerWorker>.Instance);

        // 3. Execute ProcessTimeoutsAsync directly on empty database
        // Real repository and real ExpiryWorkflowService must run without DI or missing service failures
        await scanner.ProcessTimeoutsAsync(CancellationToken.None);
    }
}
