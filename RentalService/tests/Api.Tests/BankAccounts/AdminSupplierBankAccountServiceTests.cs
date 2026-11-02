using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.BankAccounts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dprental.RentalService.Api.Tests.BankAccounts;

public sealed class AdminSupplierBankAccountServiceTests
{
    private const string DefaultAccountNumber = "0123456789";

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    private readonly string _dbName = $"AdminSupplierBankAccounts_{Guid.NewGuid():N}";
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly CapturingLogger _logger = new();

    private static RentalCallerContext Admin() => RentalCallerContext.ForUser(Guid.NewGuid(), roles: ["admin"]);

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private async Task<Result<AdminSupplierBankAccountResponse>> GetAsync(RentalCallerContext caller, Guid? supplierId = null)
    {
        await using RentalServiceDbContext context = NewContext();
        return await new AdminSupplierBankAccountService(new RenterBankAccountRepository(context), _logger)
            .GetDefaultAsync(supplierId ?? _supplierId, caller);
    }

    private async Task SeedAsync()
    {
        await using RentalServiceDbContext context = NewContext();
        context.RenterBankAccounts.AddRange(
            new RenterBankAccount(Guid.NewGuid(), _supplierId, "ACB", "9876543210", "Nguyen Van A", false, Now),
            new RenterBankAccount(Guid.NewGuid(), _supplierId, "VCB", DefaultAccountNumber, "Nguyen Van A", true, Now),
            new RenterBankAccount(Guid.NewGuid(), Guid.NewGuid(), "TCB", "5555555555", "Tran Thi B", true, Now));
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Admin_GetsDefaultAccountOfThatSupplier()
    {
        await SeedAsync();

        Result<AdminSupplierBankAccountResponse> result = await GetAsync(Admin());

        Assert.True(result.IsSuccess);
        Assert.Equal(new AdminSupplierBankAccountResponse("VCB", DefaultAccountNumber, "Nguyen Van A"), result.Value);
    }

    [Fact]
    public async Task SupplierWithoutAccount_ReturnsNotFound()
    {
        await SeedAsync();

        Result<AdminSupplierBankAccountResponse> result = await GetAsync(Admin(), Guid.NewGuid());

        Assert.Equal("BankAccount.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task NonAdminCallers_AreRejected()
    {
        await SeedAsync();

        Assert.Equal("Auth.Forbidden", (await GetAsync(RentalCallerContext.ForUser(_supplierId))).Error.Code);
        Assert.Equal(
            "Auth.ServiceTokenNotAllowed",
            (await GetAsync(RentalCallerContext.ForService("payment", roles: ["admin"]))).Error.Code);
        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public async Task EveryView_IsLogged_WithoutAccountNumber()
    {
        await SeedAsync();
        RentalCallerContext admin = Admin();

        await GetAsync(admin);

        string message = Assert.Single(_logger.Messages);
        Assert.Contains(admin.UserId!.Value.ToString(), message);
        Assert.Contains(_supplierId.ToString(), message);
        Assert.DoesNotContain(DefaultAccountNumber, message);
    }

    private sealed class CapturingLogger : ILogger<AdminSupplierBankAccountService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
