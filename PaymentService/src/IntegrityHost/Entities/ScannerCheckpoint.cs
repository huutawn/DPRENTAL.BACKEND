using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.PaymentService.IntegrityHost.Entities;

[Table("integrity_scanner_checkpoints")]
public sealed class ScannerCheckpoint
{
    [Key]
    [MaxLength(64)]
    public string ScannerName { get; private set; } = string.Empty;

    [MaxLength(128)]
    public string LastProcessedStreamId { get; private set; } = string.Empty;

    public long LastProcessedVersion { get; private set; }
    public long TotalStreamsChecked { get; private set; }
    public long TotalEntriesVerified { get; private set; }
    public DateTimeOffset LastRunAt { get; private set; }

    [MaxLength(32)]
    public string Status { get; private set; } = "IDLE";

    private ScannerCheckpoint() { }

    public static ScannerCheckpoint Create(string scannerName, DateTimeOffset now)
    {
        return new ScannerCheckpoint
        {
            ScannerName = scannerName.Trim(),
            LastProcessedStreamId = string.Empty,
            LastProcessedVersion = 0,
            TotalStreamsChecked = 0,
            TotalEntriesVerified = 0,
            LastRunAt = now.ToUniversalTime(),
            Status = "IDLE"
        };
    }

    public void UpdateProgress(string streamId, long version, int batchStreamsChecked, int batchEntriesVerified, DateTimeOffset now)
    {
        LastProcessedStreamId = streamId.Trim();
        LastProcessedVersion = version;
        TotalStreamsChecked += batchStreamsChecked;
        TotalEntriesVerified += batchEntriesVerified;
        LastRunAt = now.ToUniversalTime();
        Status = "SCANNING";
    }

    public void CompleteCycle(DateTimeOffset now)
    {
        LastProcessedStreamId = string.Empty;
        LastProcessedVersion = 0;
        LastRunAt = now.ToUniversalTime();
        Status = "COMPLETED_CYCLE";
    }
}
