using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.BankAccounts;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.BankAccounts;

public sealed class RenterBankAccountServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 3, 0, 0, TimeSpan.Zero);

    private readonly string _dbName = $"RenterBankAccounts_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly MutableTimeProvider _clock = new(Now);

    private static CreateRenterBankAccountRequest Account(string accountNumber = "0123456789", string bankCode = " VCB ") =>
        new(bankCode, accountNumber, " Nguyễn Văn A ");

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private RenterBankAccountService CreateService(RentalServiceDbContext context, IOrderLockManager? locks = null) =>
        new(new RenterBankAccountRepository(context), locks ?? new NoOpLockManager(), context, _clock);

    private async Task<RenterBankAccountResponse> AddAsync(CreateRenterBankAccountRequest request, Guid? renterId = null)
    {
        await using RentalServiceDbContext context = NewContext();
        Result<RenterBankAccountResponse> result = await CreateService(context).CreateAsync(renterId ?? _renterId, request);
        Assert.True(result.IsSuccess);
        // Mỗi tài khoản thêm sau một phút để thứ tự "mới nhất" rõ ràng.
        _clock.Advance(TimeSpan.FromMinutes(1));
        return result.Value;
    }

    private async Task<IReadOnlyList<RenterBankAccountResponse>> ListAsync(Guid? renterId = null)
    {
        await using RentalServiceDbContext context = NewContext();
        return await CreateService(context).ListAsync(renterId ?? _renterId);
    }

    [Fact]
    public async Task Create_FirstAccountBecomesDefault_AndInputIsTrimmed()
    {
        RenterBankAccountResponse first = await AddAsync(Account());
        RenterBankAccountResponse second = await AddAsync(Account("9876543210"));

        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);
        Assert.Equal("VCB", first.BankCode);
        Assert.Equal("Nguyễn Văn A", first.AccountHolderName);
    }

    [Fact]
    public async Task List_ShowsDefaultFirst_ThenNewest_AndOnlyOwnAccounts()
    {
        RenterBankAccountResponse first = await AddAsync(Account());
        RenterBankAccountResponse second = await AddAsync(Account("9876543210"));
        RenterBankAccountResponse third = await AddAsync(Account("1111222233"));
        await AddAsync(Account(), renterId: Guid.NewGuid());

        IReadOnlyList<RenterBankAccountResponse> list = await ListAsync();

        Assert.Equal([first.Id, third.Id, second.Id], list.Select(a => a.Id));
    }

    [Fact]
    public async Task Get_ReturnsOwnAccount_AndHidesOtherRenters()
    {
        RenterBankAccountResponse account = await AddAsync(Account());

        await using RentalServiceDbContext context = NewContext();
        RenterBankAccountService service = CreateService(context);

        Assert.Equal(account, (await service.GetAsync(_renterId, account.Id)).Value);
        Assert.Equal(RenterBankAccountErrors.NotFound, (await service.GetAsync(Guid.NewGuid(), account.Id)).Error);
    }

    [Fact]
    public async Task Create_RejectsSameBankAndAccountNumber_ButAllowsItForAnotherBankOrRenter()
    {
        await AddAsync(Account());

        await using (RentalServiceDbContext context = NewContext())
        {
            Result<RenterBankAccountResponse> duplicate = await CreateService(context).CreateAsync(_renterId, Account(bankCode: "VCB"));
            Assert.Equal(RenterBankAccountErrors.Duplicate, duplicate.Error);
        }

        await AddAsync(Account(bankCode: "TCB"));
        await AddAsync(Account(), renterId: Guid.NewGuid());
        Assert.Equal(2, (await ListAsync()).Count);
    }

    [Fact]
    public async Task Create_RejectsTheEleventhAccount()
    {
        for (int i = 0; i < RenterBankAccountService.MaxBankAccounts; i++)
        {
            await AddAsync(Account($"10000000{i:D2}"));
        }

        await using RentalServiceDbContext context = NewContext();
        Result<RenterBankAccountResponse> result = await CreateService(context).CreateAsync(_renterId, Account("2000000000"));

        Assert.Equal(RenterBankAccountErrors.LimitReached, result.Error);
        Assert.Equal(RenterBankAccountService.MaxBankAccounts, (await ListAsync()).Count);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("0123-456-789")]
    [InlineData("")]
    public async Task Create_RejectsInvalidAccountNumber_WithoutSaving(string accountNumber)
    {
        await using RentalServiceDbContext context = NewContext();
        Result<RenterBankAccountResponse> result = await CreateService(context).CreateAsync(_renterId, Account(accountNumber));

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("BankAccount.InvalidRequest", result.Error.Code);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task AnotherRenter_CannotDeleteOrSetDefault()
    {
        RenterBankAccountResponse account = await AddAsync(Account());
        Guid stranger = Guid.NewGuid();

        await using RentalServiceDbContext context = NewContext();
        RenterBankAccountService service = CreateService(context);

        Assert.Equal(RenterBankAccountErrors.NotFound, (await service.DeleteAsync(stranger, account.Id)).Error);
        Assert.Equal(RenterBankAccountErrors.NotFound, (await service.SetDefaultAsync(stranger, account.Id)).Error);
        Assert.Equal(account, Assert.Single(await ListAsync()));
    }

    [Fact]
    public async Task SetDefault_LeavesExactlyOneDefault()
    {
        RenterBankAccountResponse first = await AddAsync(Account());
        RenterBankAccountResponse second = await AddAsync(Account("9876543210"));

        await using RentalServiceDbContext context = NewContext();
        Result<RenterBankAccountResponse> result = await CreateService(context).SetDefaultAsync(_renterId, second.Id);

        Assert.True(result.Value.IsDefault);
        IReadOnlyList<RenterBankAccountResponse> list = await ListAsync();
        Assert.Equal(second.Id, Assert.Single(list, a => a.IsDefault).Id);
        Assert.Equal(second.Id, list[0].Id);
        Assert.Contains(list, a => a.Id == first.Id && !a.IsDefault);
    }

    [Fact]
    public async Task DeleteDefault_MakesTheNewestRemainingAccountDefault()
    {
        RenterBankAccountResponse first = await AddAsync(Account());
        await AddAsync(Account("9876543210"));
        RenterBankAccountResponse newest = await AddAsync(Account("1111222233"));

        await using RentalServiceDbContext context = NewContext();
        Result result = await CreateService(context).DeleteAsync(_renterId, first.Id);

        Assert.True(result.IsSuccess);
        IReadOnlyList<RenterBankAccountResponse> list = await ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(newest.Id, Assert.Single(list, a => a.IsDefault).Id);
    }

    [Fact]
    public async Task DeleteLastAccount_LeavesAnEmptyBook()
    {
        RenterBankAccountResponse only = await AddAsync(Account());

        await using RentalServiceDbContext context = NewContext();
        Assert.True((await CreateService(context).DeleteAsync(_renterId, only.Id)).IsSuccess);

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task BusyRenterLock_ReturnsConflict_WithoutSaving()
    {
        await using RentalServiceDbContext context = NewContext();
        Result<RenterBankAccountResponse> result = await CreateService(context, new BusyLockManager())
            .CreateAsync(_renterId, Account());

        Assert.Equal(RenterBankAccountErrors.Busy, result.Error);
        Assert.Empty(await ListAsync());
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoOpLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BusyLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new TimeoutException();
    }
}
