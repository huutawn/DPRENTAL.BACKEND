using Dprental.ReviewService.Common.Enums;

namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Thực thể ghi nhật ký kiểm toán mọi thay đổi đối với đánh giá.
/// </summary>
public sealed class ReviewAudit
{
    /// <summary>
    /// Khởi tạo mặc định cho EF Core.
    /// </summary>
    public ReviewAudit()
    {
    }

    /// <summary>
    /// Khởi tạo đầy đủ bản ghi kiểm toán đánh giá.
    /// </summary>
    public ReviewAudit(
        Guid id,
        Guid reviewId,
        Guid actorId,
        string actorRole,
        ReviewAuditAction action,
        string? reason,
        string? oldValues,
        string? newValues,
        DateTimeOffset createdAt)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        ReviewId = reviewId;
        ActorId = actorId;
        ActorRole = actorRole ?? string.Empty;
        Action = action;
        Reason = reason;
        OldValues = oldValues;
        NewValues = newValues;
        CreatedAt = createdAt;
    }

    /// <summary>Mã định danh bản ghi kiểm toán (UUID).</summary>
    public Guid Id { get; set; }

    /// <summary>Mã đánh giá liên kết.</summary>
    public Guid ReviewId { get; set; }

    /// <summary>Mã người thực hiện thao tác (JWT sub claim).</summary>
    public Guid ActorId { get; set; }

    /// <summary>Vai trò người thực hiện: Renter, Supplier, Admin, System.</summary>
    public string ActorRole { get; set; } = string.Empty;

    /// <summary>Hành động kiểm toán: Created, Updated, Withdrawn, Hidden, Restored.</summary>
    public ReviewAuditAction Action { get; set; }

    /// <summary>Lý do thực hiện thao tác (nếu có).</summary>
    public string? Reason { get; set; }

    /// <summary>Dữ liệu trước thay đổi (JSON).</summary>
    public string? OldValues { get; set; }

    /// <summary>Dữ liệu sau thay đổi (JSON).</summary>
    public string? NewValues { get; set; }

    /// <summary>Thời điểm ghi nhận kiểm toán (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Tham chiếu đến đánh giá liên quan.</summary>
    public Review? Review { get; set; }
}
