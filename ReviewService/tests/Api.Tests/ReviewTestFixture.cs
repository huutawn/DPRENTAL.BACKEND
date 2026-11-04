using System.Collections.Concurrent;
using Dprental.ReviewService.Application.Clients;
using Dprental.ReviewService.Application.DTOs.Rental;
using Dprental.ReviewService.Common;
using Dprental.ReviewService.Domain.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dprental.ReviewService.Api.Tests;

/// <summary>
/// WebApplicationFactory fixture dùng chung cho bộ kiểm thử API review với cơ sở dữ liệu in-memory và client giả lập.
/// </summary>
public sealed class ReviewTestFixture : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    public DateTimeOffset CurrentTime { get; } = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    public TestRentalServiceClient RentalClient { get; } = new();
    public TestTimeProvider TimeProvider { get; }

    public ReviewTestFixture()
    {
        TimeProvider = new TestTimeProvider(CurrentTime);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ReviewServiceDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ReviewServiceDbContext>>();
            services.RemoveAll<ReviewServiceDbContext>();
            services.AddDbContext<ReviewServiceDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<IRentalServiceClient>();
            services.AddSingleton<IRentalServiceClient>(RentalClient);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(TimeProvider);
        });
    }
}

/// <summary>
/// TimeProvider giả lập hỗ trợ kiểm soát thời gian trong các use case hết hạn (cửa sổ 30 ngày, 7 ngày).
/// </summary>
public sealed class TestTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _defaultTime;
    private DateTimeOffset? _overrideTime;

    public TestTimeProvider(DateTimeOffset defaultTime)
    {
        _defaultTime = defaultTime;
    }

    public override DateTimeOffset GetUtcNow() => _overrideTime ?? _defaultTime;

    public void SetUtcNow(DateTimeOffset time) => _overrideTime = time;

    public void Reset() => _overrideTime = null;
}

/// <summary>
/// Client giả lập giao tiếp với RentalService cho phép nạp snapshot phục vụ kiểm thử tích hợp.
/// </summary>
public sealed class TestRentalServiceClient : IRentalServiceClient
{
    private readonly ConcurrentDictionary<(Guid OrderId, Guid ItemId), RentalItemReviewSnapshotDto> _snapshots = new();

    public void SetSnapshot(RentalItemReviewSnapshotDto snapshot)
    {
        _snapshots[(snapshot.OrderId, snapshot.ItemId)] = snapshot;
    }

    public Task<Result<RentalItemReviewSnapshotDto>> GetReviewSnapshotAsync(
        Guid orderId,
        Guid itemId,
        CancellationToken cancellationToken = default)
    {
        if (_snapshots.TryGetValue((orderId, itemId), out var snapshot))
        {
            return Task.FromResult(Result.Success(snapshot));
        }

        return Task.FromResult(Result.Failure<RentalItemReviewSnapshotDto>(
            Error.NotFound("RENTAL_ITEM_NOT_FOUND", "Mục thuê không tồn tại trong hệ thống RentalService.")));
    }
}
