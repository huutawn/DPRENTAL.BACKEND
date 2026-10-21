#pragma warning disable CA1848
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Application.Services.Integrity;
using Dprental.PaymentService.Domain.IRepositories;
using Dprental.PaymentService.Domain.Repositories;
using Dprental.PaymentService.IntegrityHost.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.IntegrityHost.Services;

public sealed class IntegrityPeriodicScannerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<IntegrityHostOptions> _options;
    private readonly ILogger<IntegrityPeriodicScannerWorker> _logger;
    private readonly TimeProvider _timeProvider;

    public IntegrityPeriodicScannerWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<IntegrityHostOptions> options,
        ILogger<IntegrityPeriodicScannerWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntegrityPeriodicScannerWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int intervalSec = _options.CurrentValue.PeriodicScanIntervalSeconds > 0
                    ? _options.CurrentValue.PeriodicScanIntervalSeconds
                    : 60;

                await Task.Delay(TimeSpan.FromSeconds(intervalSec), _timeProvider, stoppingToken);

                await ScanAuditHistoryAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running integrity audit periodic scan.");
            }
        }

        _logger.LogInformation("IntegrityPeriodicScannerWorker stopped.");
    }

    public async Task<int> ScanAuditHistoryAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetService<IIntegrityScannerService>();
        if (service is not null)
        {
            return await service.ScanAuditHistoryAsync(cancellationToken);
        }

        var evidenceRepo = scope.ServiceProvider.GetService<IIntegrityEvidenceRepository>()
            ?? new Dprental.PaymentService.Domain.Repositories.Integrity.EfIntegrityEvidenceRepository(scope.ServiceProvider.GetRequiredService<IntegrityDbContext>());
        var paymentRepo = scope.ServiceProvider.GetRequiredService<ITransactionJournalRepository>();

        var scannerService = new IntegrityScannerService(evidenceRepo, paymentRepo, _timeProvider, null);
        return await scannerService.ScanAuditHistoryAsync(cancellationToken);
    }
}
