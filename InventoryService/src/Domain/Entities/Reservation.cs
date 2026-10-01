using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.ValueObjects;

namespace Dprental.InventoryService.Domain.Entities;

[Table("reservations", Schema = "inventory")]
public sealed partial class Reservation
{
    public const int MaxPhotosPerGroup = 5;
    public const int MaxManualCancellationReasonLength = 500;
    public const int MaxExternalCancellationReasonLength = 200;
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }
    [Column("reservation_number")]
    public long ReservationNumber { get; private set; }
    [NotMapped]
    public string ReservationCode => ReservationNumber > 0 ? $"LD-{ReservationNumber:D6}" : "LD-PENDING";
    [Column("equipment_id")]
    public Guid EquipmentId { get; private set; }
    [Column("source_type")]
    public ReservationSourceType SourceType { get; private set; }
    [Column("source_system")]
    [MaxLength(100)]
    public string? SourceSystem { get; private set; }
    [Column("external_reference")]
    [MaxLength(200)]
    public string? ExternalReference { get; private set; }
    public RentalPeriod RentalPeriod { get; private set; } = null!;
    [Column("renter_account_id")]
    public Guid? RenterAccountId { get; private set; }
    [Column("manual_renter_name")]
    [MaxLength(100)]
    public string? ManualRenterName { get; private set; }
    [Column("manual_renter_phone")]
    [MaxLength(12)]
    public string? ManualRenterPhone { get; private set; }
    [Column("notes")]
    [MaxLength(500)]
    public string? Notes { get; private set; }
    [Column("status")]
    public ReservationStatus Status { get; private set; }
    [Column("hold_expires_at")]
    public DateTimeOffset? HoldExpiresAt { get; private set; }
    [Column("delivered_at")]
    public DateTimeOffset? DeliveredAt { get; private set; }
    [Column("delivered_by")]
    [MaxLength(200)]
    public string? DeliveredBy { get; private set; }
    [Column("delivery_condition")]
    [MaxLength(500)]
    public string? DeliveryCondition { get; private set; }
    private List<Guid> _deliveryPhotoIds = [];
    [NotMapped]
    public IReadOnlyList<Guid> DeliveryPhotoIds => _deliveryPhotoIds.AsReadOnly();
    [Column("returned_at")]
    public DateTimeOffset? ReturnedAt { get; private set; }
    [Column("returned_by")]
    [MaxLength(200)]
    public string? ReturnedBy { get; private set; }
    [Column("return_condition_notes")]
    [MaxLength(1000)]
    public string? ReturnConditionNotes { get; private set; }

    [Column("return_condition")]
    public EquipmentReturnCondition? ReturnCondition { get; private set; }
    private List<Guid> _returnPhotoIds = [];
    [NotMapped]
    public IReadOnlyList<Guid> ReturnPhotoIds => _returnPhotoIds.AsReadOnly();
    [Column("cancelled_at")]
    public DateTimeOffset? CancelledAt { get; private set; }
    [Column("cancellation_reason")]
    [MaxLength(500)]
    public string? CancellationReason { get; private set; }
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }
    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }
    [ForeignKey(nameof(EquipmentId))]
    public InventoryEquipment Equipment { get; private set; } = null!;
    private readonly List<ReservationTransition> _transitions = [];
    public IReadOnlyCollection<ReservationTransition> Transitions => _transitions.AsReadOnly();
    private readonly List<DamageReport> _damageReports = [];
    public IReadOnlyCollection<DamageReport> DamageReports => _damageReports.AsReadOnly();
    private Reservation()
    {
    }
    public static Result<Reservation> CreateHold(
        Guid id,
        Guid equipmentId,
        RentalPeriod period,
        DateTimeOffset holdExpiresAt,
        DateTimeOffset now,
        string? sourceSystem,
        string? externalReference,
        Guid? renterAccountId,
        string? notes = null,
        string actor = "system",
        string? operationKey = null)
    {
        ArgumentNullException.ThrowIfNull(period);

        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_RESERVATION_ID", "Reservation ID không được rỗng.");
        }

        if (equipmentId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_ID", "Equipment ID không được rỗng.");
        }

        DateOnly today = BusinessDateTime.ToVietnamDate(now);
        var horizonCheck = period.ValidateBusinessHorizon(today);
        if (horizonCheck.IsFailure)
        {
            return horizonCheck.Error;
        }

        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(period.StartDate);
        if (holdExpiresAt <= now)
        {
            return Error.Validation("HOLD_EXPIRES_IN_PAST", "Thời hạn giữ chỗ phải lớn hơn thời điểm hiện tại.");
        }

        if (holdExpiresAt >= startOfDayUtc)
        {
            return Error.Validation("HOLD_EXPIRES_AFTER_START", "Thời hạn giữ chỗ phải trước 00:00 ngày bắt đầu thuê theo giờ Việt Nam.");
        }

        if (string.IsNullOrWhiteSpace(sourceSystem) || sourceSystem.Trim().Length > 100 ||
            string.IsNullOrWhiteSpace(externalReference) || externalReference.Trim().Length > 200 ||
            renterAccountId is null || renterAccountId == Guid.Empty)
        {
            return Error.Validation("INVALID_EXTERNAL_HOLD", "Hold cần nguồn, mã tham chiếu và tài khoản người thuê hợp lệ.");
        }
        if (notes?.Length > 500 || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200 ||
            operationKey is not null && (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 100))
        {
            return Error.Validation("INVALID_RESERVATION_METADATA", "Ghi chú, actor hoặc operation key không hợp lệ.");
        }
        var reservation = new Reservation
        {
            Id = id,
            EquipmentId = equipmentId,
            RentalPeriod = period,
            SourceType = ReservationSourceType.External,
            SourceSystem = sourceSystem.Trim(),
            ExternalReference = externalReference.Trim(),
            RenterAccountId = renterAccountId,
            Notes = notes?.Trim(),
            Status = ReservationStatus.Held,
            HoldExpiresAt = BusinessDateTime.NormalizeUtc(holdExpiresAt),
            CreatedAt = BusinessDateTime.NormalizeUtc(now),
            UpdatedAt = BusinessDateTime.NormalizeUtc(now)
        };

        reservation.AddTransition(
            operationType: ReservationOperationType.CreateHold,
            fromStatus: null,
            toStatus: ReservationStatus.Held,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: BusinessDateTime.NormalizeUtc(holdExpiresAt),
            operationKey: operationKey,
            requestFingerprint: null);

        return reservation;
    }
    public static Result<Reservation> CreateManual(
        Guid id,
        Guid equipmentId,
        RentalPeriod period,
        string renterName,
        string renterPhone,
        DateTimeOffset now,
        Guid? renterAccountId = null,
        string? notes = null,
        string actor = "operator",
        string? operationKey = null)
    {
        ArgumentNullException.ThrowIfNull(period);

        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_RESERVATION_ID", "Reservation ID không được rỗng.");
        }

        if (equipmentId == Guid.Empty)
        {
            return Error.Validation("INVALID_EQUIPMENT_ID", "Equipment ID không được rỗng.");
        }

        if (string.IsNullOrWhiteSpace(renterName))
        {
            return Error.Validation("RENTER_NAME_REQUIRED", "Tên khách thuê không được để trống khi tạo thủ công.");
        }

        if (string.IsNullOrWhiteSpace(renterPhone))
        {
            return Error.Validation("RENTER_PHONE_REQUIRED", "Số điện thoại khách thuê không được để trống khi tạo thủ công.");
        }

        DateOnly today = BusinessDateTime.ToVietnamDate(now);
        var horizonCheck = period.ValidateBusinessHorizon(today);
        if (horizonCheck.IsFailure)
        {
            return horizonCheck.Error;
        }

        if (renterName.Trim().Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(renterPhone.Trim(), @"^(0|\+84)[0-9]{9}$") ||
            notes?.Length > 500 || renterAccountId == Guid.Empty || string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 200 ||
            operationKey is not null && (string.IsNullOrWhiteSpace(operationKey) || operationKey.Trim().Length > 100))
        {
            return Error.Validation("INVALID_MANUAL_RESERVATION", "Thông tin người thuê, ghi chú hoặc actor không hợp lệ.");
        }
        var reservation = new Reservation
        {
            Id = id,
            EquipmentId = equipmentId,
            RentalPeriod = period,
            SourceType = ReservationSourceType.Manual,
            ManualRenterName = renterName.Trim(),
            ManualRenterPhone = renterPhone.Trim(),
            RenterAccountId = renterAccountId,
            Notes = notes?.Trim(),
            Status = ReservationStatus.Confirmed,
            HoldExpiresAt = null,
            CreatedAt = BusinessDateTime.NormalizeUtc(now),
            UpdatedAt = BusinessDateTime.NormalizeUtc(now)
        };

        reservation.AddTransition(
            operationType: ReservationOperationType.CreateManual,
            fromStatus: null,
            toStatus: ReservationStatus.Confirmed,
            now: now,
            actor: actor,
            reason: null,
            previousHoldExpiresAt: null,
            newHoldExpiresAt: null,
            operationKey: operationKey,
            requestFingerprint: null);

        return reservation;
    }
    public bool IsTerminalState() =>
        Status is ReservationStatus.Released or ReservationStatus.Cancelled or ReservationStatus.Completed;
    // Attempt time is excluded; actual handover/return time and semantic payload are included.
    private Result? CheckReplay(string? key, ReservationOperationType operation, object?[] payload, out string fingerprint)
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

    public Result ValidateExternalRetry(Guid equipmentId, RentalPeriod period, Guid renterAccountId,
        DateTimeOffset holdExpiresAt, string? notes = null) =>
        SourceType == ReservationSourceType.External && EquipmentId == equipmentId && RentalPeriod == period &&
        RenterAccountId == renterAccountId && _transitions.First(t => t.OperationType == ReservationOperationType.CreateHold).NewHoldExpiresAt == BusinessDateTime.NormalizeUtc(holdExpiresAt) &&
        Notes == notes?.Trim()
            ? Result.Success()
            : Error.Conflict("EXTERNAL_REFERENCE_REUSED", "Mã tham chiếu đã được dùng với payload khác.");

    private void AddTransition(
        ReservationOperationType operationType,
        ReservationStatus? fromStatus,
        ReservationStatus toStatus,
        DateTimeOffset now,
        string actor,
        string? reason,
        DateTimeOffset? previousHoldExpiresAt,
        DateTimeOffset? newHoldExpiresAt,
        string? operationKey,
        string? requestFingerprint)
    {
        int nextSequence = _transitions.Count == 0 ? 1 : _transitions.Max(t => t.Sequence) + 1;
        var transition = new ReservationTransition(
            id: Guid.Empty,
            reservationId: Id,
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
