using Dprental.RentalService.Application.DTOs.Addresses;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.Services.Addresses;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Addresses;

public sealed class RenterAddressServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 3, 0, 0, TimeSpan.Zero);

    private readonly string _dbName = $"RenterAddresses_{Guid.NewGuid():N}";
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly MutableTimeProvider _clock = new(Now);

    private static SaveRenterAddressRequest Site(string detail = "Công trường KCN VSIP 2, Thủ Dầu Một") =>
        new(" Bình Dương ", detail, "Nguyễn Văn A", "0901234567");

    private RentalServiceDbContext NewContext() => new(
        new DbContextOptionsBuilder<RentalServiceDbContext>().UseInMemoryDatabase(_dbName).Options);

    private RenterAddressService CreateService(RentalServiceDbContext context, IOrderLockManager? locks = null) =>
        new(new RenterAddressRepository(context), locks ?? new NoOpLockManager(), context, _clock);

    private async Task<RenterAddressResponse> AddAsync(SaveRenterAddressRequest request, Guid? renterId = null)
    {
        await using RentalServiceDbContext context = NewContext();
        Result<RenterAddressResponse> result = await CreateService(context).CreateAsync(renterId ?? _renterId, request);
        Assert.True(result.IsSuccess);
        // Mỗi địa chỉ thêm sau một phút để thứ tự "mới nhất" rõ ràng.
        _clock.Advance(TimeSpan.FromMinutes(1));
        return result.Value;
    }

    private async Task<IReadOnlyList<RenterAddressResponse>> ListAsync(Guid? renterId = null)
    {
        await using RentalServiceDbContext context = NewContext();
        return await CreateService(context).ListAsync(renterId ?? _renterId);
    }

    [Fact]
    public async Task Create_FirstAddressBecomesDefault_AndInputIsTrimmed()
    {
        RenterAddressResponse first = await AddAsync(Site());
        RenterAddressResponse second = await AddAsync(Site("Kho bãi số 5, Dĩ An"));

        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);
        Assert.Equal("Bình Dương", first.Province);
    }

    [Fact]
    public async Task List_ShowsDefaultFirst_ThenNewest_AndOnlyOwnAddresses()
    {
        RenterAddressResponse first = await AddAsync(Site());
        RenterAddressResponse second = await AddAsync(Site("Kho bãi số 5, Dĩ An"));
        RenterAddressResponse third = await AddAsync(Site("Công trường cầu Rạch Miễu 2"));
        await AddAsync(Site(), renterId: Guid.NewGuid());

        IReadOnlyList<RenterAddressResponse> list = await ListAsync();

        Assert.Equal([first.Id, third.Id, second.Id], list.Select(a => a.Id));
    }

    [Fact]
    public async Task Get_ReturnsOwnAddress_AndHidesOtherRenters()
    {
        RenterAddressResponse address = await AddAsync(Site());

        await using RentalServiceDbContext context = NewContext();
        RenterAddressService service = CreateService(context);

        Assert.Equal(address, (await service.GetAsync(_renterId, address.Id)).Value);
        Assert.Equal(RenterAddressErrors.NotFound, (await service.GetAsync(Guid.NewGuid(), address.Id)).Error);
    }

    [Fact]
    public async Task Create_RejectsTheEleventhAddress()
    {
        for (int i = 0; i < RenterAddressService.MaxAddresses; i++)
        {
            await AddAsync(Site($"Công trường số {i + 1}, Thủ Dầu Một"));
        }

        await using RentalServiceDbContext context = NewContext();
        Result<RenterAddressResponse> result = await CreateService(context).CreateAsync(_renterId, Site());

        Assert.Equal(RenterAddressErrors.LimitReached, result.Error);
        Assert.Equal(RenterAddressService.MaxAddresses, (await ListAsync()).Count);
    }

    [Fact]
    public async Task Create_RejectsInvalidPhone_WithoutSaving()
    {
        await using RentalServiceDbContext context = NewContext();
        Result<RenterAddressResponse> result = await CreateService(context)
            .CreateAsync(_renterId, Site() with { RecipientPhone = "12345" });

        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal("Address.InvalidRequest", result.Error.Code);
        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task Update_ChangesOwnAddress()
    {
        RenterAddressResponse address = await AddAsync(Site());

        await using RentalServiceDbContext context = NewContext();
        Result<RenterAddressResponse> result = await CreateService(context)
            .UpdateAsync(_renterId, address.Id, Site("Cổng số 3, KCN VSIP 2"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Cổng số 3, KCN VSIP 2", Assert.Single(await ListAsync()).AddressDetail);
    }

    [Fact]
    public async Task AnotherRenter_CannotUpdateDeleteOrSetDefault()
    {
        RenterAddressResponse address = await AddAsync(Site());
        Guid stranger = Guid.NewGuid();

        await using RentalServiceDbContext context = NewContext();
        RenterAddressService service = CreateService(context);

        Assert.Equal(RenterAddressErrors.NotFound, (await service.UpdateAsync(stranger, address.Id, Site("Địa chỉ lạ, Hà Nội"))).Error);
        Assert.Equal(RenterAddressErrors.NotFound, (await service.DeleteAsync(stranger, address.Id)).Error);
        Assert.Equal(RenterAddressErrors.NotFound, (await service.SetDefaultAsync(stranger, address.Id)).Error);
        Assert.Equal(address, Assert.Single(await ListAsync()));
    }

    [Fact]
    public async Task SetDefault_LeavesExactlyOneDefault()
    {
        RenterAddressResponse first = await AddAsync(Site());
        RenterAddressResponse second = await AddAsync(Site("Kho bãi số 5, Dĩ An"));

        await using RentalServiceDbContext context = NewContext();
        Result<RenterAddressResponse> result = await CreateService(context).SetDefaultAsync(_renterId, second.Id);

        Assert.True(result.Value.IsDefault);
        IReadOnlyList<RenterAddressResponse> list = await ListAsync();
        Assert.Equal(second.Id, Assert.Single(list, a => a.IsDefault).Id);
        Assert.Equal(second.Id, list[0].Id);
        Assert.Contains(list, a => a.Id == first.Id && !a.IsDefault);
    }

    [Fact]
    public async Task DeleteDefault_MakesTheNewestRemainingAddressDefault()
    {
        RenterAddressResponse first = await AddAsync(Site());
        await AddAsync(Site("Kho bãi số 5, Dĩ An"));
        RenterAddressResponse newest = await AddAsync(Site("Công trường cầu Rạch Miễu 2"));

        await using RentalServiceDbContext context = NewContext();
        Result result = await CreateService(context).DeleteAsync(_renterId, first.Id);

        Assert.True(result.IsSuccess);
        IReadOnlyList<RenterAddressResponse> list = await ListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(newest.Id, Assert.Single(list, a => a.IsDefault).Id);
    }

    [Fact]
    public async Task DeleteLastAddress_LeavesAnEmptyBook()
    {
        RenterAddressResponse only = await AddAsync(Site());

        await using RentalServiceDbContext context = NewContext();
        Assert.True((await CreateService(context).DeleteAsync(_renterId, only.Id)).IsSuccess);

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task BusyRenterLock_ReturnsConflict_WithoutSaving()
    {
        await using RentalServiceDbContext context = NewContext();
        Result<RenterAddressResponse> result = await CreateService(context, new BusyLockManager())
            .CreateAsync(_renterId, Site());

        Assert.Equal(RenterAddressErrors.Busy, result.Error);
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
