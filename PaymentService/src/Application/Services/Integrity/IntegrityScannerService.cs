#pragma warning disable CA1848
using Dprental.PaymentService.Application.IServices;
using Dprental.PaymentService.Domain.Entities.Integrity;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.PaymentService.Application.Services.Integrity;

public sealed class IntegrityScannerService : IIntegrityScannerService
{
    private readonly IIntegrityEvidenceRepository _evidenceRepo;
    private readonly ITransactionJournalRepository _paymentRepo;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IntegrityScannerService>? _logger;
    private readonly int _batchSize;

    public IntegrityScannerService(
        IIntegrityEvidenceRepository evidenceRepo,
        ITransactionJournalRepository paymentRepo,
        TimeProvider? timeProvider = null,
        ILogger<IntegrityScannerService>? logger = null,
        int batchSize = 500)
    {
        _evidenceRepo = evidenceRepo ?? throw new ArgumentNullException(nameof(evidenceRepo));
        _paymentRepo = paymentRepo ?? throw new ArgumentNullException(nameof(paymentRepo));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _batchSize = batchSize > 0 ? batchSize : 500;
    }

    public async Task<int> ScanAuditHistoryAsync(CancellationToken cancellationToken = default)
    {
        const string scannerName = "MainPeriodicScanner";
        var now = _timeProvider.GetUtcNow();

        var checkpoint = await _evidenceRepo.GetScannerCheckpointAsync(scannerName, cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = ScannerCheckpoint.Create(scannerName, now);
            await _evidenceRepo.AddScannerCheckpointAsync(checkpoint, cancellationToken);
            await _evidenceRepo.SaveChangesAsync(cancellationToken);
        }

        string lastStreamId = checkpoint.LastProcessedStreamId ?? string.Empty;
        var manifests = await _evidenceRepo.GetActiveManifestsAfterStreamIdAsync(lastStreamId, _batchSize, cancellationToken);

        if (manifests.Count == 0)
        {
            if (!string.IsNullOrEmpty(lastStreamId))
            {
                checkpoint.CompleteCycle(now);
                await _evidenceRepo.SaveChangesAsync(cancellationToken);
            }
            return 0;
        }

        int checkedStreams = 0;
        int verifiedEntries = 0;
        string latestProcessedStreamId = lastStreamId;
        long latestProcessedVersion = checkpoint.LastProcessedVersion;

        foreach (var manifest in manifests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var paymentLatest = await _paymentRepo.GetLatestEntryForStreamAsync(manifest.StreamId, cancellationToken);

            // 1. Detect missing or deleted records
            if (paymentLatest is null)
            {
                manifest.Block("Stream completely missing from business database (data deletion / rollback).", now);
                await _evidenceRepo.AddIncidentAsync(IntegrityIncident.Create(
                    "ROLLBACK_DETECTED",
                    manifest.StreamId,
                    manifest.LatestVersion,
                    manifest.LatestEventId,
                    $"Stream {manifest.StreamId} exists in integrity manifest at version {manifest.LatestVersion} but missing from Payment DB.",
                    now), cancellationToken);
                await _evidenceRepo.SaveChangesAsync(cancellationToken);
                continue;
            }

            // 2. Detect rollback (version regressed in Payment DB)
            if (paymentLatest.Version < manifest.LatestVersion)
            {
                manifest.Block($"Database rollback detected: manifest version {manifest.LatestVersion}, business DB at version {paymentLatest.Version}.", now);
                await _evidenceRepo.AddIncidentAsync(IntegrityIncident.Create(
                    "ROLLBACK_DETECTED",
                    manifest.StreamId,
                    paymentLatest.Version,
                    paymentLatest.EventId,
                    $"Rollback detected on stream {manifest.StreamId}: manifest is at version {manifest.LatestVersion}, but payment DB is at version {paymentLatest.Version}.",
                    now), cancellationToken);
                await _evidenceRepo.SaveChangesAsync(cancellationToken);
                continue;
            }

            // 3. Verify confirmed prefix matches manifest
            if (paymentLatest.Version == manifest.LatestVersion)
            {
                if (!string.Equals(paymentLatest.EntryHash, manifest.LatestEntryHash, StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Block($"Hash mismatch on stream {manifest.StreamId} at confirmed version {manifest.LatestVersion}.", now);
                    await _evidenceRepo.AddIncidentAsync(IntegrityIncident.Create(
                        "UNAUTHORIZED_EDIT",
                        manifest.StreamId,
                        paymentLatest.Version,
                        paymentLatest.EventId,
                        $"Latest entry hash in Payment DB ({paymentLatest.EntryHash}) does not match confirmed manifest hash ({manifest.LatestEntryHash}).",
                        now), cancellationToken);
                    await _evidenceRepo.SaveChangesAsync(cancellationToken);
                    continue;
                }
                verifiedEntries++;
            }
            else if (paymentLatest.Version > manifest.LatestVersion)
            {
                // Payment has newer pending versions: verify the confirmed prefix version
                var confirmedVersionEntry = await _paymentRepo.GetByStreamAndVersionAsync(manifest.StreamId, manifest.LatestVersion, cancellationToken);
                if (confirmedVersionEntry is null || !string.Equals(confirmedVersionEntry.EntryHash, manifest.LatestEntryHash, StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Block($"Prefix hash mismatch on stream {manifest.StreamId} at confirmed version {manifest.LatestVersion}.", now);
                    await _evidenceRepo.AddIncidentAsync(IntegrityIncident.Create(
                        "UNAUTHORIZED_EDIT",
                        manifest.StreamId,
                        manifest.LatestVersion,
                        manifest.LatestEventId,
                        $"Confirmed prefix version {manifest.LatestVersion} in Payment DB was altered or deleted.",
                        now), cancellationToken);
                    await _evidenceRepo.SaveChangesAsync(cancellationToken);
                    continue;
                }
                verifiedEntries++;
            }

            checkedStreams++;
            latestProcessedStreamId = manifest.StreamId;
            latestProcessedVersion = manifest.LatestVersion;
        }

        checkpoint.UpdateProgress(latestProcessedStreamId, latestProcessedVersion, checkedStreams, verifiedEntries, now);
        await _evidenceRepo.SaveChangesAsync(cancellationToken);

        return checkedStreams;
    }
}
