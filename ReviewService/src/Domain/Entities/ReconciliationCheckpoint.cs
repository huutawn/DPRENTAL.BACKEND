namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Lưu vết tiến trình quét đối soát định kỳ (Full-Window Sweep Checkpoints).
/// </summary>
public sealed class ReconciliationCheckpoint
{
    public string SweepId { get; private set; } = string.Empty;
    public DateTimeOffset WindowStart { get; private set; }
    public Guid? LastItemId { get; private set; }
    public bool IsCompleted { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ReconciliationCheckpoint() { }

    public ReconciliationCheckpoint(
        string sweepId,
        DateTimeOffset windowStart,
        Guid? lastItemId,
        bool isCompleted,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt)
    {
        SweepId = sweepId;
        WindowStart = windowStart.ToUniversalTime();
        LastItemId = lastItemId;
        IsCompleted = isCompleted;
        StartedAt = startedAt.ToUniversalTime();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    public void Advance(Guid? lastItemId, bool isCompleted, DateTimeOffset updatedAt)
    {
        LastItemId = lastItemId;
        IsCompleted = isCompleted;
        UpdatedAt = updatedAt.ToUniversalTime();
    }
}
