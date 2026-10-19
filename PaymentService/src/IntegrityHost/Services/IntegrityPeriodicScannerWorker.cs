#pragma warning disable CA1848
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.IntegrityHost.Entities;
using Dprental.PaymentService.IntegrityHost.Options;
using Dprental.PaymentService.IntegrityHost.Persistence;
using Microsoft.EntityFrameworkCore;
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
        var integrityDb = scope.ServiceProvider.GetRequiredService<IntegrityDbContext>();
        var paymentRepo = scope.ServiceProvider.GetRequiredService<ITransactionJournalRepository>();

        var manifests = await integrityDb.Manifests
            .Where(m => !m.IsBlocked)
            .ToListAsync(cancellationToken);

        int checkedStreams = 0;
        var now = _timeProvider.GetUtcNow();

        foreach (var manifest in manifests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var paymentLatest = await paymentRepo.GetLatestEntryForStreamAsync(manifest.StreamId, cancellationToken);

            // 1. Detect missing or deleted records
            if (paymentLatest is null)
            {
                manifest.Block("Stream completely missing from business database (data deletion / rollback).", now);
                integrityDb.Incidents.Add(IntegrityIncident.Create(
                    "ROLLBACK_DETECTED",
                    manifest.StreamId,
                    manifest.LatestVersion,
                    manifest.LatestEventId,
                    $"Stream {manifest.StreamId} exists in integrity manifest at version {manifest.LatestVersion} but missing from Payment DB.",
                    now));
                await integrityDb.SaveChangesAsync(cancellationToken);
                continue;
            }

            // 2. Detect rollback (version regressed in Payment DB)
            if (paymentLatest.Version < manifest.LatestVersion)
            {
                manifest.Block($"Database rollback detected: manifest version {manifest.LatestVersion}, business DB at version {paymentLatest.Version}.", now);
                integrityDb.Incidents.Add(IntegrityIncident.Create(
                    "ROLLBACK_DETECTED",
                    manifest.StreamId,
                    paymentLatest.Version,
                    paymentLatest.EventId,
                    $"Rollback detected on stream {manifest.StreamId}: manifest is at version {manifest.LatestVersion}, but payment DB is at version {paymentLatest.Version}.",
                    now));
                await integrityDb.SaveChangesAsync(cancellationToken);
                continue;
            }

            // 3. Verify confirmed prefix matches manifest
            if (paymentLatest.Version == manifest.LatestVersion)
            {
                if (!string.Equals(paymentLatest.EntryHash, manifest.LatestEntryHash, StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Block($"Hash mismatch on stream {manifest.StreamId} at confirmed version {manifest.LatestVersion}.", now);
                    integrityDb.Incidents.Add(IntegrityIncident.Create(
                        "UNAUTHORIZED_EDIT",
                        manifest.StreamId,
                        paymentLatest.Version,
                        paymentLatest.EventId,
                        $"Latest entry hash in Payment DB ({paymentLatest.EntryHash}) does not match confirmed manifest hash ({manifest.LatestEntryHash}).",
                        now));
                    await integrityDb.SaveChangesAsync(cancellationToken);
                    continue;
                }
            }
            else if (paymentLatest.Version > manifest.LatestVersion)
            {
                // Payment has newer pending versions: verify the confirmed prefix version
                var confirmedVersionEntry = await paymentRepo.GetByStreamAndVersionAsync(manifest.StreamId, manifest.LatestVersion, cancellationToken);
                if (confirmedVersionEntry is null || !string.Equals(confirmedVersionEntry.EntryHash, manifest.LatestEntryHash, StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Block($"Prefix hash mismatch on stream {manifest.StreamId} at confirmed version {manifest.LatestVersion}.", now);
                    integrityDb.Incidents.Add(IntegrityIncident.Create(
                        "UNAUTHORIZED_EDIT",
                        manifest.StreamId,
                        manifest.LatestVersion,
                        manifest.LatestEventId,
                        $"Confirmed prefix version {manifest.LatestVersion} in Payment DB was altered or deleted.",
                        now));
                    await integrityDb.SaveChangesAsync(cancellationToken);
                    continue;
                }
            }

            checkedStreams++;
        }

        return checkedStreams;
    }
}
