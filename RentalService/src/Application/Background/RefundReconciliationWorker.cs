using Dprental.RentalService.Application.IServices.Refunds;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Background;

/// <summary>Repairs interrupted confirmations and mirrors direct Payment confirmations without repeating transfers.</summary>
public sealed partial class RefundReconciliationWorker(
    IServiceScopeFactory scopes, ILogger<RefundReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Guid? cursor = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IRentalRefundRepository>();
                var ids = await repository.GetReconciliationItemIdsAsync(cursor, 50, stoppingToken);
                // Advance past conflicts too; one inconsistent refund cannot starve later pages.
                cursor = ids.Count == 0 ? null : ids[^1];
                foreach (var id in ids)
                {
                    using var itemScope = scopes.CreateScope();
                    var service = itemScope.ServiceProvider.GetRequiredService<IAdminRefundCommandService>();
                    var result = await service.ReconcileAsync(id, true, stoppingToken);
                    if (result.IsFailure) LogFailure(logger, id, result.Error.Code);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { LogUnexpected(logger, exception); }
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund reconciliation for item {ItemId} requires retry or review: {Code}")]
    private static partial void LogFailure(ILogger logger, Guid itemId, string code);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Refund reconciliation batch failed")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
