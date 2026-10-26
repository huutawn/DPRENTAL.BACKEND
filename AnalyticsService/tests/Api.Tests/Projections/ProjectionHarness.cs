using Dprental.AnalyticsService.Application.Services.Projections;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Projections;

/// <summary>
/// DB InMemory riêng cho mỗi test + repository thật + 3 projector. Mỗi lần Apply xong thì lưu,
/// giống B5 sẽ làm cho từng message, nên các event sau đọc được dữ liệu event trước đã lưu.
/// </summary>
internal sealed class ProjectionHarness : IDisposable
{
    public ProjectionHarness()
    {
        DbContextOptions<AnalyticsServiceDbContext> options = new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        Context = new AnalyticsServiceDbContext(options);

        RentalFactRepository rentalRepository = new(Context);
        Orders = new RentalOrderProjector(rentalRepository, Changes);
        Items = new RentalItemProjector(rentalRepository, Changes);
        Financial = new FinancialProjector(new FinancialFactRepository(Context));
    }

    public AnalyticsServiceDbContext Context { get; }
    public ReadModelChangeSet Changes { get; } = new();
    public RentalOrderProjector Orders { get; }
    public RentalItemProjector Items { get; }
    public FinancialProjector Financial { get; }

    /// <summary>Chạy projector; thành công thì lưu, thất bại thì bỏ mọi thay đổi (như rollback).</summary>
    public async Task<Result> ApplyAsync(Func<Task<Result>> apply)
    {
        Result result = await apply();
        if (result.IsSuccess)
        {
            await Context.SaveChangesAsync();
        }
        else
        {
            Context.ChangeTracker.Clear();
        }

        Context.ChangeTracker.Clear(); // Lần đọc sau lấy từ "DB", không dùng object cũ trong bộ nhớ
        return result;
    }

    public async Task ApplyOkAsync(Func<Task<Result>> apply)
    {
        Result result = await ApplyAsync(apply);
        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);
    }

    public void Dispose() => Context.Dispose();
}
