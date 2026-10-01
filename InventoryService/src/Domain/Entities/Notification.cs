using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể ghi nhận thông báo vận hành gửi đến toàn thể tổ chức quản lý thiết bị.
/// Trạng thái đã xem (Viewed) dùng chung cho toàn tổ chức. Thông báo có thời hạn hết hạn 90 ngày.
/// </summary>
[Table("notifications", Schema = "inventory")]
public sealed class Notification
{
    /// <summary>
    /// Số ngày mặc định trước khi thông báo tự động hết hạn (90 ngày).
    /// </summary>
    public const int DefaultExpiryDays = 90;

    /// <summary>
    /// Định danh duy nhất toàn cục của bản ghi thông báo.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Định danh tổ chức nhận thông báo này.
    /// </summary>
    [Column("organization_id")]
    public Guid OrganizationId { get; private set; }

    /// <summary>
    /// Phân loại loại sự kiện thông báo (HoldExpired, Cancelled, DeliveryDue, ReturnOverdue, DamageReport...).
    /// </summary>
    [Column("notification_type")]
    public NotificationType Type { get; private set; }

    /// <summary>
    /// Định danh lượt đặt lịch liên quan nếu thông báo bắt nguồn từ sự kiện của reservation.
    /// </summary>
    [Column("reservation_id")]
    public Guid? ReservationId { get; private set; }

    /// <summary>
    /// Định danh báo cáo hư hỏng liên quan nếu thông báo phát sinh từ sự cố thiết bị.
    /// </summary>
    [Column("damage_report_id")]
    public Guid? DamageReportId { get; private set; }

    /// <summary>
    /// Khóa chống trùng lặp (Deduplication Key) được tạo theo tổ chức và sự kiện nghiệp vụ, duy nhất trong cùng tổ chức.
    /// </summary>
    [Column("deduplication_key")]
    [MaxLength(200)]
    public string DeduplicationKey { get; private set; } = string.Empty;

    /// <summary>
    /// Nội dung chi tiết của thông báo gửi tới tổ chức.
    /// </summary>
    [Column("content")]
    [MaxLength(2000)]
    public string Content { get; private set; } = string.Empty;

    /// <summary>
    /// Thời điểm khởi tạo thông báo (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm hết hạn của thông báo (UTC), mặc định 90 ngày sau khi tạo.
    /// </summary>
    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>
    /// Thời điểm thành viên trong tổ chức xem thông báo lần đầu tiên (UTC).
    /// </summary>
    [Column("viewed_at")]
    public DateTimeOffset? ViewedAt { get; private set; }

    /// <summary>
    /// Định danh hoặc tên thành viên trong tổ chức đã xem thông báo.
    /// </summary>
    [Column("viewed_by")]
    [MaxLength(200)]
    public string? ViewedBy { get; private set; }

    /// <summary>
    /// Concurrency token ánh xạ tới xmin của PostgreSQL phục vụ phát hiện stale updates.
    /// </summary>
    [Timestamp]
    [Column("xmin")]
    public uint Version { get; set; }

    /// <summary>
    /// Lượt đặt lịch liên kết nếu có.
    /// </summary>
    [ForeignKey(nameof(ReservationId))]
    public Reservation? Reservation { get; private set; }

    /// <summary>
    /// Báo cáo sự cố liên kết nếu có.
    /// </summary>
    [ForeignKey(nameof(DamageReportId))]
    public DamageReport? DamageReport { get; private set; }

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private Notification()
    {
    }

    /// <summary>
    /// Khởi tạo thông báo mới.
    /// </summary>
    private Notification(
        Guid id,
        Guid organizationId,
        NotificationType type,
        string deduplicationKey,
        string content,
        DateTimeOffset now,
        Guid? reservationId,
        Guid? damageReportId)
    {
        Id = id;
        OrganizationId = organizationId;
        Type = type;
        DeduplicationKey = deduplicationKey;
        Content = content;
        CreatedAt = now;
        ExpiresAt = now.AddDays(DefaultExpiryDays);
        ReservationId = reservationId;
        DamageReportId = damageReportId;
    }

    /// <summary>
    /// Tạo mới một thông báo vận hành cho tổ chức với khóa chống trùng lặp.
    /// </summary>
    /// <param name="id">ID duy nhất của thông báo.</param>
    /// <param name="organizationId">ID tổ chức nhận thông báo.</param>
    /// <param name="type">Loại thông báo.</param>
    /// <param name="deduplicationKey">Khóa chống trùng lặp duy nhất theo sự kiện.</param>
    /// <param name="content">Nội dung thông báo.</param>
    /// <param name="now">Thời điểm tạo thông báo (UTC).</param>
    /// <param name="reservationId">ID reservation liên quan nếu có.</param>
    /// <param name="damageReportId">ID damage report liên quan nếu có.</param>
    /// <returns>Result chứa Notification hoặc lỗi validation.</returns>
    public static Result<Notification> Create(
        Guid id,
        Guid organizationId,
        NotificationType type,
        string deduplicationKey,
        string content,
        DateTimeOffset now,
        Guid? reservationId = null,
        Guid? damageReportId = null)
    {
        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_NOTIFICATION_ID", "Notification ID không được rỗng.");
        }

        if (organizationId == Guid.Empty)
        {
            return Error.Validation("INVALID_ORGANIZATION_ID", "Organization ID không được rỗng.");
        }

        if (string.IsNullOrWhiteSpace(deduplicationKey))
        {
            return Error.Validation("DEDUPLICATION_KEY_REQUIRED", "Cần cung cấp Deduplication Key cho thông báo.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Error.Validation("CONTENT_REQUIRED", "Nội dung thông báo không được để trống.");
        }

        return new Notification(
            id: id,
            organizationId: organizationId,
            type: type,
            deduplicationKey: deduplicationKey.Trim(),
            content: content.Trim(),
            now: now,
            reservationId: reservationId,
            damageReportId: damageReportId);
    }

    /// <summary>
    /// Đánh dấu thông báo đã được xem bởi một thành viên trong tổ chức.
    /// </summary>
    /// <param name="viewer">Tên hoặc định danh người xem.</param>
    /// <param name="now">Thời điểm xem (UTC).</param>
    /// <returns>Result thành công hoặc lỗi validation.</returns>
    public Result MarkAsViewed(string viewer, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(viewer))
        {
            return Error.Validation("VIEWER_REQUIRED", "Cần cung cấp thông tin người xem thông báo.");
        }

        // Nếu đã xem trước đó thì giữ nguyên lần xem đầu tiên của tổ chức
        if (!ViewedAt.HasValue)
        {
            ViewedAt = now;
            ViewedBy = viewer.Trim();
        }

        return Result.Success();
    }

    /// <summary>
    /// Kiểm tra xem thông báo này đã hết hạn hay chưa tại một thời điểm cho trước.
    /// </summary>
    /// <param name="now">Thời điểm kiểm tra (UTC).</param>
    /// <returns>True nếu đã hết hạn, ngược lại False.</returns>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}
