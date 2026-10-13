using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Api.Search;

/// <summary>
/// Hosted service chạy khi ứng dụng khởi động: gọi provisioner cấu hình index,
/// kiểm tra generation và tự động kích hoạt backfill nếu chưa có generation hoàn chỉnh.
/// Không chặn API khởi động; trong thời gian chưa sẵn sàng, API tự động dùng SQL fallback.
/// </summary>
public sealed class SearchBootstrapHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchBootstrapHostedService> _logger;

    public SearchBootstrapHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<SearchBootstrapHostedService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var provisioner = scope.ServiceProvider.GetRequiredService<IInventoryIndexProvisioner>();
                var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
                var backfillService = scope.ServiceProvider.GetRequiredService<IInventorySearchBackfillService>();

                _logger.LogInformation("Bắt đầu bootstrap cấu hình chỉ mục Meilisearch...");
                await provisioner.EnsureIndexesAndSettingsAsync(CancellationToken.None);

                var gen = await store.GetGenerationAsync(CancellationToken.None);
                if (gen is null || !gen.IsReady)
                {
                    _logger.LogInformation("Chưa có search generation live hoàn chỉnh. Tự động kích hoạt backfill ban đầu...");
                    await backfillService.BackfillAsync(CancellationToken.None);
                }

                _logger.LogInformation("Bootstrap Meilisearch thành công.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bootstrap Meilisearch chưa hoàn tất. Hệ thống hoạt động an toàn qua SQL fallback.");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
