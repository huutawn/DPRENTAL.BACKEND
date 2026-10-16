#pragma warning disable CA1848, CA1873
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Background;

public sealed class OperationRecoveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationRecoveryWorker> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string[] _excludedActions;

    public OperationRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OperationRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:OperationRecovery:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
        var excluded = new List<string>
        {
            RentalItem.ApplyReturnedUnitsAction,
            RentalItem.MarkReturnedAction,
            RentalItem.BlockDamagedAction,
            "MarkAssetsHandedOver",
            "ConfirmAllocation",
            "ExtendHold",
            "ReleaseAllocation"
        };
        if (!configuration.GetValue("Rental:OverdueProtection:Enabled", true))
            excluded.Add(RentalItem.OverdueProtectionAction);
        _excludedActions = excluded.ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OperationRecoveryWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in OperationRecoveryWorker loop");
            }

            try
            {
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OperationRecoveryWorker stopping");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        IReadOnlyList<OperationReceiptDescriptor> descriptors;

        using (var scope = _scopeFactory.CreateScope())
        {
            var receiptRepository = scope.ServiceProvider.GetRequiredService<IOperationReceiptRepository>();
            descriptors = _excludedActions.Length == 0
                ? await receiptRepository.GetPendingOperationDescriptorsAsync(utcNow, 50, cancellationToken)
                : await receiptRepository.GetPendingOperationDescriptorsExcludingActionsAsync(
                    utcNow, 50, _excludedActions, cancellationToken);
        }

        if (descriptors.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("OperationRecoveryWorker recovering {Count} pending operations", descriptors.Count);

        int processed = 0;
        foreach (var descriptor in descriptors)
        {
            try
            {
                using var opScope = _scopeFactory.CreateScope();
                var executor = opScope.ServiceProvider.GetRequiredService<IRentalOperationExecutor>();
                var result = await executor.ExecuteScheduledOperationAsync(descriptor, cancellationToken);
                if (result.IsSuccess)
                {
                    processed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recover operation {OpId} ({Action})", descriptor.OperationId, descriptor.Action);
            }
        }

        // Reconcile handover/return commands pending > 30 minutes without acknowledgement
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Dprental.RentalService.Domain.Repositories.RentalServiceDbContext>();
            DateTimeOffset timeoutThreshold = utcNow.AddMinutes(-30);
            var staleReceipts = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.RentalOperationReceipts.Where(r =>
                    (r.Status == RentalOperationReceipt.StatusPending || r.Status == RentalOperationReceipt.StatusProcessing) &&
                    (r.Action == "MarkAssetsHandedOver" ||
                     r.Action == RentalItem.ApplyReturnedUnitsAction ||
                     r.Action == RentalItem.MarkReturnedAction ||
                     r.Action == RentalItem.BlockDamagedAction) &&
                    r.CreatedAt <= timeoutThreshold),
                cancellationToken);

            if (staleReceipts.Count > 0)
            {
                foreach (var r in staleReceipts)
                {
                    r.MarkManualReconciliationRequired(utcNow, "Command acknowledgement timed out after 30 minutes; moved to manual reconciliation.");
                }
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogWarning("Marked {Count} stale handover/return receipts as MANUAL_RECONCILIATION_REQUIRED", staleReceipts.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reconciling stale handover/return receipts");
        }
        return processed;
    }
}
