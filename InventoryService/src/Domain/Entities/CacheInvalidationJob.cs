using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể công việc xóa cache (Cache Invalidation Job) lưu trữ các scope cần đổi generation token độc lập với Kafka.
/// </summary>
[Table("cache_invalidation_jobs", Schema = "inventory")]
public sealed class CacheInvalidationJob
{
    public const string StatusPending = "Pending";
    public const string StatusProcessing = "Processing";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";

    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Required]
    [Column("scopes", TypeName = "text[]")]
    public string[] Scopes { get; private set; } = Array.Empty<string>();

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("retry_count")]
    public int RetryCount { get; private set; }

    [Column("next_attempt_at")]
    public DateTimeOffset NextAttemptAt { get; private set; }

    [Column("lock_lease_token")]
    public Guid? LockLeaseToken { get; private set; }

    [Column("lock_lease_expires_at")]
    public DateTimeOffset? LockLeaseExpiresAt { get; private set; }

    [Required]
    [MaxLength(20)]
    [Column("status")]
    public string Status { get; private set; } = StatusPending;

    [Column("processed_at")]
    public DateTimeOffset? ProcessedAt { get; private set; }

    [Column("last_error")]
    public string? LastError { get; private set; }

    [Column("fanout_owner_user_id")]
    public Guid? FanoutOwnerUserId { get; private set; }

    [Column("fanout_after_listing_id")]
    public Guid? FanoutAfterListingId { get; private set; }

    public bool IsFanout => FanoutOwnerUserId.HasValue;

    // EF Core constructor
    private CacheInvalidationJob()
    {
    }

    public static CacheInvalidationJob Create(
        Guid id,
        IEnumerable<string> scopes,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var scopeArray = scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (scopeArray.Length == 0)
        {
            throw new ArgumentException("Danh sách scopes không được rỗng.", nameof(scopes));
        }

        return new CacheInvalidationJob
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            Scopes = scopeArray,
            CreatedAt = now,
            RetryCount = 0,
            NextAttemptAt = now,
            Status = StatusPending
        };
    }

    public static CacheInvalidationJob CreateFanout(
        Guid id,
        Guid ownerUserId,
        IEnumerable<string>? initialScopes,
        DateTimeOffset now)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("OwnerUserId không được để trống khi tạo fanout job.", nameof(ownerUserId));
        }

        var scopeArray = (initialScopes ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new CacheInvalidationJob
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            FanoutOwnerUserId = ownerUserId,
            FanoutAfterListingId = null,
            Scopes = scopeArray,
            CreatedAt = now,
            RetryCount = 0,
            NextAttemptAt = now,
            Status = StatusPending
        };
    }

    public void CheckpointFanout(Guid afterListingId, DateTimeOffset now)
    {
        FanoutAfterListingId = afterListingId;
        Scopes = Array.Empty<string>(); // Scopes ban đầu đã được invalidate
        Status = StatusPending;
        LockLeaseToken = null;
        LockLeaseExpiresAt = null;
        NextAttemptAt = now;
        LastError = null;
    }

    public void AssignLease(Guid leaseToken, DateTimeOffset leaseExpiresAt)
    {
        LockLeaseToken = leaseToken;
        LockLeaseExpiresAt = leaseExpiresAt;
        Status = StatusProcessing;
    }

    public void MarkCompleted(DateTimeOffset now)
    {
        Status = StatusCompleted;
        LockLeaseToken = null;
        LockLeaseExpiresAt = null;
        ProcessedAt = now;
        LastError = null;
    }

    public void MarkFailed(int retryCount, DateTimeOffset nextAttemptAt, string? error, bool isTerminal)
    {
        RetryCount = retryCount;
        NextAttemptAt = nextAttemptAt;
        Status = isTerminal ? StatusFailed : StatusPending;
        LockLeaseToken = null;
        LockLeaseExpiresAt = null;
        LastError = error;
    }
}
