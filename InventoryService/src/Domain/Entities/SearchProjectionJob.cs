using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể công việc đồng bộ chỉ mục tìm kiếm với desired/processing/applied revision, lease và task tracking.
/// </summary>
[Table("search_projection_jobs", Schema = "inventory")]
public sealed class SearchProjectionJob
{
    public const string StatusPending = "Pending";
    public const string StatusProcessing = "Processing";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";

    public const string ActionUpsert = "Upsert";
    public const string ActionDelete = "Delete";

    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Required]
    [MaxLength(50)]
    [Column("entity_type")]
    public string EntityType { get; private set; } = string.Empty;

    [Column("entity_id")]
    public Guid EntityId { get; private set; }

    [Required]
    [MaxLength(20)]
    [Column("action")]
    public string Action { get; private set; } = ActionUpsert;

    /// <summary>Revision mới nhất do mutation yêu cầu.</summary>
    [Column("desired_revision")]
    public long DesiredRevision { get; private set; }

    /// <summary>Revision đang được worker xử lý trong lease hiện tại.</summary>
    [Column("processing_revision")]
    public long? ProcessingRevision { get; private set; }

    /// <summary>Revision đã được xác nhận cập nhật thành công lên Meilisearch.</summary>
    [Column("applied_revision")]
    public long AppliedRevision { get; private set; }

    /// <summary>Cờ đánh dấu cần fan-out cập nhật danh sách asset con của listing.</summary>
    [Column("needs_asset_fanout")]
    public bool NeedsAssetFanout { get; private set; }

    /// <summary>Cursor keyset ID của asset con đã duyệt trong lượt fan-out.</summary>
    [Column("fanout_cursor")]
    public Guid? FanoutCursor { get; private set; }

    /// <summary>Đã hoàn tất duyệt fan-out toàn bộ asset con hay chưa.</summary>
    [Column("fanout_completed")]
    public bool FanoutCompleted { get; private set; }

    [Required]
    [MaxLength(20)]
    [Column("status")]
    public string Status { get; private set; } = StatusPending;

    [Column("retry_count")]
    public int RetryCount { get; private set; }

    [Column("lock_lease_token")]
    public Guid? LockLeaseToken { get; private set; }

    [Column("lock_lease_expires_at")]
    public DateTimeOffset? LockLeaseExpiresAt { get; private set; }

    [Column("next_attempt_at")]
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>Danh sách task UID Meilisearch đang chờ xử lý.</summary>
    [Column("pending_task_uids", TypeName = "integer[]")]
    public int[] PendingTaskUids { get; private set; } = Array.Empty<int>();

    [Column("submission_revision")]
    public long? SubmissionRevision { get; private set; }

    [Column("submission_started")]
    public bool SubmissionStarted { get; private set; }

    [Column("submission_uncertain")]
    public bool SubmissionUncertain { get; private set; }

    [Column("submission_complete")]
    public bool SubmissionComplete { get; private set; }

    [Column("fanout_revision")]
    public long? FanoutRevision { get; private set; }

    [Column("fanout_requested_revision")]
    public long FanoutRequestedRevision { get; private set; }

    [Column("fanout_applied_revision")]
    public long FanoutAppliedRevision { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    [Column("error_message")]
    public string? ErrorMessage { get; private set; }

    private SearchProjectionJob() { }

    public static SearchProjectionJob Create(
        Guid id,
        string entityType,
        Guid entityId,
        string action,
        long desiredRevision,
        bool needsAssetFanout,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return new SearchProjectionJob
        {
            Id = id,
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            DesiredRevision = desiredRevision,
            ProcessingRevision = null,
            AppliedRevision = 0,
            NeedsAssetFanout = needsAssetFanout,
            FanoutRequestedRevision = needsAssetFanout ? desiredRevision : 0,
            FanoutCursor = null,
            FanoutCompleted = !needsAssetFanout,
            Status = StatusPending,
            RetryCount = 0,
            PendingTaskUids = Array.Empty<int>(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
