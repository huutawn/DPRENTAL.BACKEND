using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.Addresses;
using Dprental.RentalService.Application.Services.Addresses;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Addresses;

/// <summary>
/// Sổ địa chỉ trên PostgreSQL thật: bảng do migration tạo, thứ tự đọc và khóa theo người thuê.
/// </summary>
public sealed class RenterAddressPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentFirstAddresses_LeaveExactlyOneDefault()
    {
        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL integration environment is not available at test connection string.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            DbContextOptions<RentalServiceDbContext> options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            await using (RentalServiceDbContext migrate = new(options))
            {
                await migrate.Database.MigrateAsync();
            }

            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
                    .Build(),
                NullLogger<PostgresOrderLockManager>.Instance);
            Guid renterId = Guid.NewGuid();

            async Task<Result<RenterAddressResponse>> CreateAsync(string detail)
            {
                await using RentalServiceDbContext context = new(options);
                RenterAddressService service = new(new RenterAddressRepository(context), locks, context, new FixedTimeProvider(Now));
                return await service.CreateAsync(renterId, new("Bình Dương", detail, "Nguyễn Văn A", "0901234567"));
            }

            // Hai thiết bị cùng thêm địa chỉ đầu tiên: nếu không khóa, cả hai đều thấy sổ rỗng và cùng thành mặc định.
            Result<RenterAddressResponse>[] results = await Task.WhenAll(
                Task.Run(() => CreateAsync("Công trường KCN VSIP 2, Thủ Dầu Một")),
                Task.Run(() => CreateAsync("Kho bãi số 5, Dĩ An")));

            Assert.All(results, r => Assert.True(r.IsSuccess));
            await using RentalServiceDbContext read = new(options);
            IReadOnlyList<RenterAddressResponse> list = await new RenterAddressService(
                new RenterAddressRepository(read), locks, read, new FixedTimeProvider(Now)).ListAsync(renterId);
            Assert.Equal(2, list.Count);
            Assert.Single(list, a => a.IsDefault);
            Assert.True(list[0].IsDefault);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
