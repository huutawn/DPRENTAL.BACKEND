using Dprental.RentalService.Api.Tests.Checkout;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.Services.BankAccounts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dprental.RentalService.Api.Tests.BankAccounts;

/// <summary>
/// Sổ tài khoản ngân hàng trên PostgreSQL thật: bảng và unique index do migration tạo, khóa theo người thuê.
/// </summary>
public sealed class RenterBankAccountPostgreSqlTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentFirstAccounts_LeaveExactlyOneDefault()
    {
        await WithMigratedDatabaseAsync(async (connectionString, options) =>
        {
            PostgresOrderLockManager locks = new(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString })
                    .Build(),
                NullLogger<PostgresOrderLockManager>.Instance);
            Guid renterId = Guid.NewGuid();

            async Task<Result<RenterBankAccountResponse>> CreateAsync(string accountNumber)
            {
                await using RentalServiceDbContext context = new(options);
                RenterBankAccountService service = new(new RenterBankAccountRepository(context), locks, context, new FixedTimeProvider(Now));
                return await service.CreateAsync(renterId, new("VCB", accountNumber, "Nguyễn Văn A"));
            }

            // Hai thiết bị cùng thêm tài khoản đầu tiên: nếu không khóa, cả hai đều thấy sổ rỗng và cùng thành mặc định.
            Result<RenterBankAccountResponse>[] results = await Task.WhenAll(
                Task.Run(() => CreateAsync("0123456789")),
                Task.Run(() => CreateAsync("9876543210")));

            Assert.All(results, r => Assert.True(r.IsSuccess));
            await using RentalServiceDbContext read = new(options);
            IReadOnlyList<RenterBankAccountResponse> list = await new RenterBankAccountService(
                new RenterBankAccountRepository(read), locks, read, new FixedTimeProvider(Now)).ListAsync(renterId);
            Assert.Equal(2, list.Count);
            Assert.Single(list, a => a.IsDefault);
            Assert.True(list[0].IsDefault);
        });
    }

    [Fact]
    public async Task UniqueIndex_RejectsSameBankAndAccountNumberForOneRenter()
    {
        await WithMigratedDatabaseAsync(async (_, options) =>
        {
            Guid renterId = Guid.NewGuid();
            await using (RentalServiceDbContext context = new(options))
            {
                context.RenterBankAccounts.Add(new RenterBankAccount(Guid.NewGuid(), renterId, "VCB", "0123456789", "Nguyễn Văn A", true, Now));
                // Người thuê khác dùng cùng số tài khoản vẫn được.
                context.RenterBankAccounts.Add(new RenterBankAccount(Guid.NewGuid(), Guid.NewGuid(), "VCB", "0123456789", "Nguyễn Văn A", true, Now));
                await context.SaveChangesAsync();
            }

            await using RentalServiceDbContext duplicate = new(options);
            duplicate.RenterBankAccounts.Add(new RenterBankAccount(Guid.NewGuid(), renterId, "VCB", "0123456789", "Nguyễn Văn A", false, Now));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        });
    }

    private static async Task WithMigratedDatabaseAsync(Func<string, DbContextOptions<RentalServiceDbContext>, Task> test)
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

            await test(connectionString, options);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }
}
