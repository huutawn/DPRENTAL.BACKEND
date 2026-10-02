using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text.Json;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể đại diện cho một lượt phân bổ hoặc giữ chỗ lịch của tài sản trong kho (Asset Allocation).
/// </summary>
[Table("asset_allocations", Schema = "inventory")]
public sealed partial class AssetAllocation
{
    public const int MaxCancellationReasonLength = 500;

    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Column("asset_id")]
    public Guid AssetId { get; private set; }

    [Column("source_system")]
    [MaxLength(100)]
    public string SourceSystem { get; private set; } = string.Empty;

    [Column("external_reference")]
    [MaxLength(200)]
    public string ExternalReference { get; private set; } = string.Empty;

    public RentalPeriod RentalPeriod { get; private set; } = null!;

    [Column("notes")]
    [MaxLength(500)]
    public string? Notes { get; private set; }

    [Column("status")]
    public AllocationStatus Status { get; private set; }

    [Column("hold_expires_at")]
    public DateTimeOffset? HoldExpiresAt { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    [ForeignKey(nameof(AssetId))]
    public InventoryAsset Asset { get; private set; } = null!;

    private readonly List<AllocationTransition> _transitions = [];
    public IReadOnlyCollection<AllocationTransition> Transitions => _transitions.AsReadOnly();

    private AssetAllocation()
    {
    }

    /// <summary>
    /// Tạo mới một lượt giữ chỗ tạm thời (Hold) cho tài sản.
    /// </summary>
    public static Result<AssetAllocation> CreateHold(
        Guid id,
        Guid assetId,
        RentalPeriod period,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset now,
        string sourceSystem,
        string externalReference,
        string? notes = null,
        string actor = "system",
        string? operationKey = null)
    {
        ArgumentNullException.ThrowIfNull(period);

        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_ALLOCATION_ID", "Allocation ID không được rỗng.");
        }

        if (assetId == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(period.StartDate);
        if (holdExpiresAt <= now)
        {
            return Error.Validation("HOLD_EXPIRES_IN_PAST", "Thời hạn giữ chỗ phải lớn hơn thời điểm hiện tại.");
        }

        if (holdExpiresAt >= startOfDayUtc)
        {
            return Error.Validation("HOLD_EXPIRES_AFTER_START", "Thời hạn giữ chỗ phải trước 00:00 ngày bắt đầu theo giờ Việt Nam.");
        }

        if (string.IsNullOrWhiteSpace(sourceSystem) || sourceSystem.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(externalReference) || externalReference.Trim().Length > 200)
        {
            return Error.Validation("INVALID_EXTERNAL_REFERENCE", "Nguồn và mã tham chiếu phân bổ không hợp lệ.");
        }

        if (notes?.Length > 500 || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200 ||
            operationKey is not null && (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 100))
        {
            return Error.Validation("INVALID_ALLOCATION_METADATA", "Ghi chú, actor hoặc operation key không hợp lệ.");
        }

        var allocation = new AssetAllocation
        {
            Id = id,
            AssetId = assetId,
            RentalPeriod = period,
            SourceSystem = sourceSystem.Trim(),
            ExternalReference = externalReference.Trim(),
            Notes = notes?.Trim(),
            Status = AllocationStatus.Held,
            HoldExpiresAt = BusinessDateTime.NormalizeUtc(holdExpiresAt),
            CreatedAt = BusinessDateTime.NormalizeUtc(now),
            UpdatedAt = BusinessDateTime.NormalizeUtc(now)
        };

        allocation.AddTransition(
            operationType: AllocationOperationType.CreateHold,
            fromStatus: null,
            toStatus: AllocationStatus.Held,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: BusinessDateTime.NormalizeUtc(holdExpiresAt),
            operationKey: operationKey,
            requestFingerprint: null);

        return allocation;
    }

    /// <summary>
    /// Tạo mới một lượt phân bổ thủ công trực tiếp (Manual Allocation) ở trạng thái Allocated.
    /// </summary>
    public static Result<AssetAllocation> CreateManual(
        Guid id,
        Guid assetId,
        RentalPeriod period,
        DateTimeOffset now,
        string? operationalReference = null,
        string? notes = null,
        string actor = "operator",
        string? operationKey = null)
    {
        ArgumentNullException.ThrowIfNull(period);

        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_ALLOCATION_ID", "Allocation ID không được rỗng.");
        }

        if (assetId == Guid.Empty)
        {
            return Error.Validation("INVALID_ASSET_ID", "Asset ID không được rỗng.");
        }

        string reference = string.IsNullOrWhiteSpace(operationalReference)
            ? id.ToString("D")
            : operationalReference.Trim();

        if (reference.Length > 200 || notes?.Length > 500 ||
            string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200 ||
            operationKey is not null && (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 100))
        {
            return Error.Validation("INVALID_MANUAL_ALLOCATION", "Tham chiếu, ghi chú hoặc actor không hợp lệ.");
        }

        var allocation = new AssetAllocation
        {
            Id = id,
            AssetId = assetId,
            RentalPeriod = period,
            SourceSystem = "inventory.manual",
            ExternalReference = reference,
            Notes = notes?.Trim(),
            Status = AllocationStatus.Allocated,
            HoldExpiresAt = null,
            CreatedAt = BusinessDateTime.NormalizeUtc(now),
            UpdatedAt = BusinessDateTime.NormalizeUtc(now)
        };

        allocation.AddTransition(
            operationType: AllocationOperationType.CreateManual,
            fromStatus: null,
            toStatus: AllocationStatus.Allocated,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: null);

        return allocation;
    }

    /// <summary>
    /// Kiểm tra trạng thái có phải là kết thúc không thể chuyển tiếp thêm.
    /// </summary>
    public bool IsTerminalState() =>
        Status is AllocationStatus.Released or AllocationStatus.Cancelled;

    private Result? CheckReplay(string? key, AllocationOperationType operation, object?[] payload, out string fingerprint)
    {
        fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload)));
        if (key is null) return null;
        if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 100)
            return Error.Validation("INVALID_OPERATION_KEY", "Operation key không hợp lệ.");
        var applied = _transitions.Find(t => t.OperationKey == key.Trim());
        if (applied is null) return null;
        return applied.OperationType == operation && applied.RequestFingerprint == fingerprint
            ? Result.Success()
            : Error.Conflict("IDEMPOTENCY_KEY_REUSED", "Operation key đã được dùng cho thao tác hoặc payload khác.");
    }

    public Result ValidateExternalRetry(Guid assetId, RentalPeriod period, DateTimeOffset holdExpiresAt, string? notes = null) =>
        AssetId == assetId && RentalPeriod == period &&
        _transitions.First(t => t.OperationType == AllocationOperationType.CreateHold).NewHoldExpiresAt == BusinessDateTime.NormalizeUtc(holdExpiresAt) &&
        Notes == notes?.Trim()
            ? Result.Success()
            : Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Mã tham chiếu đã được dùng với payload khác.");

    private void AddTransition(
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
        int nextSequence = _transitions.Count == 0 ? 1 : _transitions.Max(t => t.Sequence) + 1;
        var transition = new AllocationTransition(
            id: Guid.Empty,
            allocationId: Id,
            sequence: nextSequence,
            operationType: operationType,
            fromStatus: fromStatus,
            toStatus: toStatus,
            now: BusinessDateTime.NormalizeUtc(now),
            actor: actor.Trim(),
            reason: reason?.Trim(),
            previousHoldExpiresAt: previousHoldExpiresAt,
            newHoldExpiresAt: newHoldExpiresAt,
            operationKey: operationKey?.Trim(),
            requestFingerprint: requestFingerprint);

        _transitions.Add(transition);
    }
}
