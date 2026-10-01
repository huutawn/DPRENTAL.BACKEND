using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể ghi nhận báo cáo sự cố hư hỏng hoặc xuống cấp của thiết bị do khách thuê gửi trong thời gian thuê.
/// Bản ghi báo hỏng có tính bất biến sau khi gửi, chỉ có thể cập nhật trạng thái đã xem/đánh giá bởi tổ chức.
/// </summary>
[Table("damage_reports", Schema = "inventory")]
public sealed class DamageReport
{
    /// <summary>
    /// Số lượng ảnh tối đa cho phép đính kèm trong một báo cáo hư hỏng.
    /// </summary>
    public const int MaxPhotos = 5;

    /// <summary>
    /// Số lượng báo cáo hư hỏng tối đa cho phép tạo trên cùng một lượt đặt.
    /// </summary>
    public const int MaxDamageReportsPerReservation = 5;

    /// <summary>
    /// Định danh duy nhất toàn cục của bản ghi báo cáo hư hỏng.
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    /// <summary>
    /// Định danh lượt đặt lịch đang thuê phát sinh sự cố hư hỏng.
    /// </summary>
    [Column("reservation_id")]
    public Guid ReservationId { get; private set; }

    /// <summary>
    /// Định danh tài khoản khách thuê gửi báo cáo sự cố này.
    /// </summary>
    [Column("reporter_account_id")]
    public Guid ReporterAccountId { get; private set; }

    /// <summary>
    /// Mô tả chi tiết hiện trạng hư hỏng, sự cố hoặc nguyên nhân phát sinh.
    /// </summary>
    [Column("description")]
    [MaxLength(1000)]
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Đánh giá của khách thuê về việc thiết bị có còn tiếp tục sử dụng được hay không.
    /// </summary>
    [Column("is_usable")]
    public bool IsUsable { get; private set; }

    /// <summary>
    /// Danh sách Media IDs ảnh chụp hiện trường hư hỏng (tối đa 5 ảnh).
    /// </summary>
    private List<Guid> _photoMediaIds = [];
    [NotMapped]
    public IReadOnlyList<Guid> PhotoMediaIds => _photoMediaIds.AsReadOnly();

    /// <summary>
    /// Thời điểm gửi báo cáo hư hỏng lên hệ thống (UTC).
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Thời điểm tổ chức/nhân viên xem xét và đánh giá báo cáo này (UTC).
    /// </summary>
    [Column("reviewed_at")]
    public DateTimeOffset? ReviewedAt { get; private set; }

    /// <summary>
    /// Định danh hoặc tên nhân viên/quản trị viên thực hiện xem xét báo cáo.
    /// </summary>
    [Column("reviewed_by")]
    [MaxLength(200)]
    public string? ReviewedBy { get; private set; }

    /// <summary>
    /// Lượt đặt lịch liên kết mà sự cố này thuộc về.
    /// </summary>
    [ForeignKey(nameof(ReservationId))]
    public Reservation Reservation { get; private set; } = null!;

    /// <summary>
    /// Khởi tạo private phục vụ EF Core.
    /// </summary>
    private DamageReport()
    {
    }

    /// <summary>
    /// Khởi tạo báo cáo hư hỏng mới.
    /// </summary>
    private DamageReport(
        Guid id,
        Guid reservationId,
        Guid reporterAccountId,
        string description,
        bool isUsable,
        IReadOnlyList<Guid>? photoMediaIds,
        DateTimeOffset now)
    {
        Id = id;
        ReservationId = reservationId;
        ReporterAccountId = reporterAccountId;
        Description = description;
        IsUsable = isUsable;
        _photoMediaIds = photoMediaIds?.ToList() ?? [];
        CreatedAt = now;
    }

    /// <summary>
    /// Tạo mới một báo cáo sự cố hư hỏng với các ràng buộc nghiệp vụ chặt chẽ.
    /// Báo hỏng chỉ được chấp nhận khi reservation đang ở trạng thái InUse, đúng tài khoản khách thuê,
    /// tối đa 5 ảnh và chưa vượt quá 5 báo cáo trên cùng lượt đặt.
    /// </summary>
    /// <param name="id">ID duy nhất của báo cáo hư hỏng.</param>
    /// <param name="reservation">Đối tượng reservation liên kết.</param>
    /// <param name="reporterAccountId">ID tài khoản khách thuê gửi báo cáo.</param>
    /// <param name="description">Mô tả hiện trạng hư hỏng.</param>
    /// <param name="isUsable">Thiết bị còn dùng được không.</param>
    /// <param name="photoMediaIds">Danh sách ảnh minh chứng (tối đa 5).</param>
    /// <param name="now">Thời điểm gửi báo cáo (UTC).</param>
    /// <param name="existingReportsCountOnReservation">Số lượng báo cáo hiện có trên lượt đặt.</param>
    /// <returns>Result chứa DamageReport nếu hợp lệ hoặc lỗi nghiệp vụ.</returns>
    public static Result<DamageReport> Create(
        Guid id,
        Reservation reservation,
        Guid reporterAccountId,
        string description,
        bool isUsable,
        IReadOnlyList<Guid>? photoMediaIds,
        DateTimeOffset now,
        int existingReportsCountOnReservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        if (id == Guid.Empty)
        {
            return Error.Validation("INVALID_REPORT_ID", "Damage Report ID không được rỗng.");
        }

        if (reporterAccountId == Guid.Empty)
        {
            return Error.Validation("INVALID_REPORTER_ID", "Reporter Account ID không được rỗng.");
        }

        if (reservation.Status != ReservationStatus.InUse)
        {
            return Error.Validation("RESERVATION_NOT_IN_USE", $"Chỉ có thể báo hỏng khi lượt đặt lịch đang ở trạng thái InUse. Trạng thái hiện tại: {reservation.Status}.");
        }

        if (reservation.RenterAccountId == null || reservation.RenterAccountId.Value != reporterAccountId)
        {
            return Error.Forbidden("NOT_RESERVATION_RENTER", "Chỉ khách thuê đã liên kết với lượt đặt lịch này mới có quyền gửi báo cáo hư hỏng.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Error.Validation("DESCRIPTION_REQUIRED", "Cần cung cấp mô tả chi tiết sự cố hư hỏng.");
        }

        if (description.Trim().Length is < 10 or > 1000)
            return Error.Validation("INVALID_DESCRIPTION_LENGTH", "Mô tả phải từ 10 đến 1000 ký tự.");
        if (existingReportsCountOnReservation < 0 || photoMediaIds?.Any(id => id == Guid.Empty) == true)
            return Error.Validation("INVALID_REPORT_METADATA", "Số báo cáo hoặc ảnh không hợp lệ.");

        if (photoMediaIds != null && photoMediaIds.Count > MaxPhotos)
        {
            return Error.Validation("TOO_MANY_PHOTOS", $"Tối đa {MaxPhotos} ảnh minh chứng hư hỏng.");
        }

        if (existingReportsCountOnReservation >= MaxDamageReportsPerReservation)
        {
            return Error.Conflict("DAMAGE_REPORT_LIMIT_EXCEEDED", $"Đã đạt giới hạn tối đa {MaxDamageReportsPerReservation} báo cáo hư hỏng trên lượt đặt này.");
        }

        return new DamageReport(
            id: id,
            reservationId: reservation.Id,
            reporterAccountId: reporterAccountId,
            description: description.Trim(),
            isUsable: isUsable,
            photoMediaIds: photoMediaIds,
            now: now);
    }

    /// <summary>
    /// Đánh dấu báo cáo hư hỏng đã được tổ chức xem xét và xử lý.
    /// </summary>
    /// <param name="reviewer">Tên hoặc định danh người xem xét.</param>
    /// <param name="now">Thời điểm xem xét (UTC).</param>
    /// <returns>Result thành công hoặc lỗi validation.</returns>
    public Result Review(string reviewer, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            return Error.Validation("REVIEWER_REQUIRED", "Cần cung cấp thông tin người xem xét báo cáo.");
        }

        ReviewedBy = reviewer.Trim();
        ReviewedAt = now;
        return Result.Success();
    }
}
