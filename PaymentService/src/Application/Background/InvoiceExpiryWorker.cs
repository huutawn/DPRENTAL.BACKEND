#pragma warning disable CA1848, CA1873
using System.Diagnostics.CodeAnalysis;
using Dprental.PaymentService.Application.IServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.PaymentService.Application.Background;

[SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Background worker.")]
public sealed class InvoiceExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InvoiceExpiryWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;
    private readonly TimeProvider _timeProvider;

    public InvoiceExpiryWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InvoiceExpiryWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        int intervalSeconds = configuration.GetValue("Workers:InvoiceExpiry:IntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        _batchSize = configuration.GetValue("Workers:InvoiceExpiry:BatchSize", 100);
    }

    public TimeSpan Interval => _interval;
    public int BatchSize => _batchSize;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InvoiceExpiryWorker started with interval {Interval}s, batch size {BatchSize}",
            _interval.TotalSeconds, _batchSize);

        using var timer = new PeriodicTimer(_interval, _timeProvider);

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
                _logger.LogWarning(ex, "Error occurred during invoice expiry processing cycle.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("InvoiceExpiryWorker stopped.");
    }

    internal async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var invoiceService = scope.ServiceProvider.GetRequiredService<IInvoiceService>();

        int expiredCount = await invoiceService.ExpirePendingInvoicesBatchAsync(_batchSize, cancellationToken);
        if (expiredCount > 0)
        {
            _logger.LogInformation("Expired {Count} pending invoices in current batch.", expiredCount);
        }
    }
}
