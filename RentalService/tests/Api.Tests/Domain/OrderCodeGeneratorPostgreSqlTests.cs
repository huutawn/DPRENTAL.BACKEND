using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class OrderCodeGeneratorPostgreSqlTests
{
    [Fact]
    public async Task NextAsync_AfterMigrations_ReturnsIncreasingCodesWithVietnamBusinessYear()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            await using RentalServiceDbContext context = new(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await context.Database.MigrateAsync();
            PostgresOrderCodeGenerator generator = new(context);

            // 17:30 UTC ngày 31/12/2026 là 00:30 ngày 01/01/2027 giờ Việt Nam.
            DateTimeOffset newYearInVietnam = new(2026, 12, 31, 17, 30, 0, TimeSpan.Zero);

            string first = await generator.NextAsync(newYearInVietnam);
            string second = await generator.NextAsync(newYearInVietnam);

            Assert.Equal("DH-2027-000001", first);
            Assert.Equal("DH-2027-000002", second);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
