#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Services.Checkout;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Background;

public sealed class CheckoutTimeoutScannerWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CheckoutTimeoutScannerWorker> _logger;
    private readonly TimeSpan _pollingInterval;

    public CheckoutTimeoutScannerWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CheckoutTimeoutScannerWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:CheckoutTimeoutScanner:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CheckoutTimeoutScannerWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

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
                await ProcessTimeoutsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in CheckoutTimeoutScannerWorker loop");
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

        _logger.LogInformation("CheckoutTimeoutScannerWorker stopping");
    }

    private async Task ProcessTimeoutsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var operationRepo = scope.ServiceProvider.GetRequiredService<IRentalCheckoutOperationRepository>();
        var lockManager = scope.ServiceProvider.GetRequiredService<IOrderLockManager>();
        var inventoryCommands = scope.ServiceProvider.GetRequiredService<IInventoryCommandProducer>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var timeProvider = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

        var utcNow = timeProvider.GetUtcNow();
        var timedOutOps = await operationRepo.GetTimedOutOperationsAsync(utcNow, limit: 50, cancellationToken);

        if (timedOutOps.Count == 0) return;

        foreach (var op in timedOutOps)
        {
            if (cancellationToken.IsCancellationRequested) break;

            IAsyncDisposable renterLock;
            try
            {
                renterLock = await lockManager.AcquireRenterLockAsync(op.RenterUserId, TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                continue;
            }

            await using (renterLock)
            await using (var orderLock = await lockManager.AcquireOrderLockAsync(op.ExpectedRentalOrderId, TimeSpan.FromSeconds(5), cancellationToken))
            {
                var current = await operationRepo.GetByOperationIdAsync(op.OperationId, cancellationToken);
                if (current == null || current.Status != CheckoutOperationStatus.Processing || current.HoldExpiresAt > utcNow)
                {
                    continue;
                }

                current.MarkTimedOut(utcNow);

                var lineStatuses = JsonSerializer.Deserialize<List<CheckoutItemHoldDescriptor>>(current.ItemHoldStatusesJson, JsonOptions);
                if (lineStatuses != null)
                {
                    foreach (var line in lineStatuses)
                    {
                        var releaseCmd = new ReleaseAllocationCommandPayload(
                            RentalOrderId: current.ExpectedRentalOrderId,
                            RentalItemId: line.RentalItemId,
                            OperationKey: $"checkout:timeout:{current.ExpectedRentalOrderId:N}:{line.RentalItemId:N}:release",
                            Reason: "Checkout hold window expired (30 minutes)");

                        await inventoryCommands.EnqueueReleaseAllocationAsync(
                            releaseCmd,
                            correlationId: current.ExpectedRentalOrderId.ToString("D"),
                            cancellationToken: cancellationToken);
                    }
                }

                await unitOfWork.CommitAsync(cancellationToken);
                _logger.LogInformation("Checkout operation {OperationId} timed out and compensated", current.OperationId);
            }
        }
    }
}
