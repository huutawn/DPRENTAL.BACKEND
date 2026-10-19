using Dprental.PaymentService.Domain.Entities.Integrity;
using Dprental.PaymentService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.PaymentService.Domain.Repositories.Integrity;

public sealed class EfIntegrityEvidenceRepository : IIntegrityEvidenceRepository
{
    private readonly IntegrityDbContext _db;

    public EfIntegrityEvidenceRepository(IntegrityDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<StreamManifest?> GetManifestAsync(string streamId, CancellationToken ct = default)
    {
        return _db.Manifests.FirstOrDefaultAsync(m => m.StreamId == streamId, ct);
    }

    public async Task<IReadOnlyList<StreamManifest>> GetActiveManifestsAfterStreamIdAsync(string lastStreamId, int take, CancellationToken ct = default)
    {
#pragma warning disable CA1309
        var query = _db.Manifests.Where(m => !m.IsBlocked);
        if (!string.IsNullOrEmpty(lastStreamId))
        {
            query = query.Where(m => string.Compare(m.StreamId, lastStreamId) > 0);
        }
        return await query
            .OrderBy(m => m.StreamId)
            .Take(take)
            .ToListAsync(ct);
#pragma warning restore CA1309
    }

    public Task AddManifestAsync(StreamManifest manifest, CancellationToken ct = default)
    {
        _db.Manifests.Add(manifest);
        return Task.CompletedTask;
    }

    public Task<AttestationRecord?> GetAttestationAsync(string streamId, long version, CancellationToken ct = default)
    {
        return _db.Attestations.FirstOrDefaultAsync(a => a.StreamId == streamId && a.Version == version, ct);
    }

    public Task AddAttestationAsync(AttestationRecord attestation, CancellationToken ct = default)
    {
        _db.Attestations.Add(attestation);
        return Task.CompletedTask;
    }

    public Task AddIncidentAsync(IntegrityIncident incident, CancellationToken ct = default)
    {
        _db.Incidents.Add(incident);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<IntegrityIncident>> GetRecentIncidentsAsync(int take, CancellationToken ct = default)
    {
        return await _db.Incidents
            .AsNoTracking()
            .OrderByDescending(i => i.DetectedAt)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<ScannerCheckpoint?> GetScannerCheckpointAsync(string scannerName, CancellationToken ct = default)
    {
        return _db.Checkpoints.FirstOrDefaultAsync(c => c.ScannerName == scannerName, ct);
    }

    public Task AddScannerCheckpointAsync(ScannerCheckpoint checkpoint, CancellationToken ct = default)
    {
        _db.Checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }

    public Task<EntityHeadRecord?> GetEntityHeadAsync(string entityType, string entityId, CancellationToken ct = default)
    {
        return _db.EntityHeads.FirstOrDefaultAsync(h => h.EntityType == entityType && h.EntityId == entityId, ct);
    }

    public Task AddEntityHeadAsync(EntityHeadRecord head, CancellationToken ct = default)
    {
        _db.EntityHeads.Add(head);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        return _db.SaveChangesAsync(ct);
    }
}
