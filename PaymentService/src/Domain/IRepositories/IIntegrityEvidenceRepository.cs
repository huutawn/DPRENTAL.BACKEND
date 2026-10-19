using Dprental.PaymentService.Domain.Entities.Integrity;

namespace Dprental.PaymentService.Domain.IRepositories;

public interface IIntegrityEvidenceRepository
{
    Task<StreamManifest?> GetManifestAsync(string streamId, CancellationToken ct = default);
    Task<IReadOnlyList<StreamManifest>> GetActiveManifestsAfterStreamIdAsync(string lastStreamId, int take, CancellationToken ct = default);
    Task AddManifestAsync(StreamManifest manifest, CancellationToken ct = default);

    Task<AttestationRecord?> GetAttestationAsync(string streamId, long version, CancellationToken ct = default);
    Task AddAttestationAsync(AttestationRecord attestation, CancellationToken ct = default);

    Task AddIncidentAsync(IntegrityIncident incident, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrityIncident>> GetRecentIncidentsAsync(int take, CancellationToken ct = default);

    Task<ScannerCheckpoint?> GetScannerCheckpointAsync(string scannerName, CancellationToken ct = default);
    Task AddScannerCheckpointAsync(ScannerCheckpoint checkpoint, CancellationToken ct = default);

    Task<EntityHeadRecord?> GetEntityHeadAsync(string entityType, string entityId, CancellationToken ct = default);
    Task AddEntityHeadAsync(EntityHeadRecord head, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
