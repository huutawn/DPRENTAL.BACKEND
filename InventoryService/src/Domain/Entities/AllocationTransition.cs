using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể ghi nhận lịch sử chuyển trạng thái của một lượt phân bổ tài sản (append-only audit log).
/// </summary>
[Table("allocation_transitions", Schema = "inventory")]
public sealed class AllocationTransition
{
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Column("allocation_id")]
    public Guid AllocationId { get; private set; }

    [Column("sequence")]
    public int Sequence { get; private set; }

    [Column("operation_type")]
    public AllocationOperationType OperationType { get; private set; }

    [Column("from_status")]
    public AllocationStatus? FromStatus { get; private set; }

    [Column("to_status")]
    public AllocationStatus ToStatus { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("actor")]
    [MaxLength(200)]
    public string Actor { get; private set; } = string.Empty;

    [Column("reason")]
    [MaxLength(1000)]
    public string? Reason { get; private set; }

    [Column("previous_hold_expires_at")]
    public DateTimeOffset? PreviousHoldExpiresAt { get; private set; }

    [Column("new_hold_expires_at")]
    public DateTimeOffset? NewHoldExpiresAt { get; private set; }

    [Column("operation_key")]
    [MaxLength(100)]
    public string? OperationKey { get; private set; }

    [Column("request_fingerprint")]
    [MaxLength(64)]
    public string? RequestFingerprint { get; private set; }

    [ForeignKey(nameof(AllocationId))]
    public AssetAllocation Allocation { get; private set; } = null!;

    private AllocationTransition()
    {
    }

    internal AllocationTransition(
        Guid id,
        Guid allocationId,
        int sequence,
        AllocationOperationType operationType,
        AllocationStatus? fromStatus,
        AllocationStatus toStatus,
        DateTimeOffset now,
        string actor,
        string? reason,
        DateTimeOffset? previousHoldExpiresAt,
        DateTimeOffset? newHoldExpiresAt,
        string? operationKey,
        string? requestFingerprint)
    {
        Id = id;
        AllocationId = allocationId;
        Sequence = sequence;
        OperationType = operationType;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        CreatedAt = now;
        Actor = actor;
        Reason = reason;
        PreviousHoldExpiresAt = previousHoldExpiresAt;
        NewHoldExpiresAt = newHoldExpiresAt;
        OperationKey = operationKey;
        RequestFingerprint = requestFingerprint;
    }
}
