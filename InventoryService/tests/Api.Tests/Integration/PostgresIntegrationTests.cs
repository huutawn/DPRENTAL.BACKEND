using Dprental.InventoryService.Api.Infrastructure.Repositories;
using System.Data;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Bộ kiểm thử tích hợp thực tế với cơ sở dữ liệu PostgreSQL (chạy qua Docker Compose).
/// Kiểm chứng toàn bộ: Migration DB rỗng, Check/Unique/FK constraints, Concurrency Protocol (pg_advisory_xact_lock),
/// Rollback atomicity, Idempotency, Concurrency Token xmin và Tenant Isolation.
/// Tuyệt đối KHÔNG sử dụng EF InMemory để kiểm chứng transactions, locks hoặc constraints.
/// </summary>
public sealed partial class PostgresIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;

    public PostgresIntegrationTests(PostgresFixture fixture) => _connectionString = fixture.ConnectionString;

    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        services.AddScoped<IInventoryAssetRepository, InventoryAssetRepository>();
        return services.BuildServiceProvider();
    }

    private Task<InventoryServiceDbContext> CreateMigratedDbContextAsync()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;

        var context = new InventoryServiceDbContext(options);
        return Task.FromResult(context);
    }

}
