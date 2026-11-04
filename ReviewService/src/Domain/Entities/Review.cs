using Dprental.ReviewService.Common.Enums;

namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Thực thể cốt lõi lưu trữ đánh giá người dùng.
/// </summary>
public sealed class Review
{
    private int _rating = 1;
    private Guid _authorId;
    private Guid _subjectId;

    /// <summary>
    /// Khởi tạo mặc định cho EF Core.
    /// </summary>
    public Review()
    {
    }

    /// <summary>
    /// Khởi tạo đầy đủ thực thể đánh giá với kiểm tra invariant nghiệp vụ.
    /// </summary>
    public Review(
        Guid id,
        Guid orderId,
        Guid itemId,
        ReviewDirection direction,
        Guid authorId,
        Guid subjectId,
        Guid equipmentListingId,
        int rating,
        string? comment,
        AuthorState authorState,
        ModerationState moderationState,
        long version,
        DateTimeOffset createdAt,
        DateTimeOffset? updatedAt,
        DateTimeOffset? withdrawnAt,
        DateTimeOffset? adminHiddenAt,
        string? adminReason,
        DateTimeOffset canEditUntil)
    {
        ValidateRating(rating);
        ValidateAuthorAndSubject(authorId, subjectId);

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        OrderId = orderId;
        ItemId = itemId;
        Direction = direction;
        _authorId = authorId;
        _subjectId = subjectId;
        EquipmentListingId = equipmentListingId;
        _rating = rating;
        Comment = comment;
        AuthorState = authorState;
        ModerationState = moderationState;
        Version = version;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        WithdrawnAt = withdrawnAt;
        AdminHiddenAt = adminHiddenAt;
        AdminReason = adminReason;
        CanEditUntil = canEditUntil;
    }

    /// <summary>Mã định danh đánh giá (UUID).</summary>
    public Guid Id { get; set; }

    /// <summary>Mã đơn thuê từ RentalService.</summary>
    public Guid OrderId { get; set; }

    /// <summary>Mã mục thuê từ RentalService.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Chiều đánh giá (renter_to_supplier_listing hoặc supplier_to_renter).</summary>
    public ReviewDirection Direction { get; set; }

    /// <summary>Mã người dùng viết đánh giá.</summary>
    public Guid AuthorId
    {
        get => _authorId;
        set
        {
            ValidateAuthorAndSubject(value, _subjectId);
            _authorId = value;
        }
    }

    /// <summary>Mã đối tượng nhận đánh giá.</summary>
    public Guid SubjectId
    {
        get => _subjectId;
        set
        {
            ValidateAuthorAndSubject(_authorId, value);
            _subjectId = value;
        }
    }

    /// <summary>Mã thiết bị được thuê từ CatalogService/RentalService.</summary>
    public Guid EquipmentListingId { get; set; }

    /// <summary>Mã thiết bị / bài đăng được thuê (alias chuẩn hóa cho EquipmentListingId).</summary>
    public Guid ListingId => EquipmentListingId;

    /// <summary>Mã nhà cung cấp đối với review (SubjectId cho renter_to_supplier_listing, AuthorId cho supplier_to_renter).</summary>
    public Guid SupplierId => Direction == ReviewDirection.renter_to_supplier_listing ? SubjectId : AuthorId;

    /// <summary>Điểm đánh giá từ 1 đến 5 sao.</summary>
    public int Rating
    {
        get => _rating;
        set
        {
            ValidateRating(value);
            _rating = value;
        }
    }

    /// <summary>Nội dung bình luận (tối đa 1.000 ký tự Unicode).</summary>
    public string? Comment { get; set; }

    /// <summary>Trạng thái của tác giả: Active hoặc Withdrawn.</summary>
    public AuthorState AuthorState { get; set; } = AuthorState.Active;

    /// <summary>Trạng thái kiểm duyệt: Visible hoặc Hidden.</summary>
    public ModerationState ModerationState { get; set; } = ModerationState.Visible;

    /// <summary>Số nguyên phiên bản dùng làm Concurrency Token (Strong ETag).</summary>
    public long Version { get; set; } = 1;

    /// <summary>Thời điểm tạo đánh giá (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Thời điểm chỉnh sửa gần nhất (UTC).</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Thời điểm tác giả rút đánh giá (UTC).</summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    /// <summary>Thời điểm quản trị viên ẩn đánh giá (UTC).</summary>
    public DateTimeOffset? AdminHiddenAt { get; set; }

    /// <summary>Lý do quản trị viên ẩn đánh giá.</summary>
    public string? AdminReason { get; set; }

    /// <summary>Hạn chót cho phép tác giả chỉnh sửa (UTC).</summary>
    public DateTimeOffset CanEditUntil { get; set; }

    /// <summary>Nhật ký kiểm toán thay đổi của đánh giá này.</summary>
    public ICollection<ReviewAudit> Audits { get; set; } = new List<ReviewAudit>();

    /// <summary>
    /// Tạo mới một thực thể đánh giá với các giá trị mặc định hợp lệ.
    /// </summary>
    public static Review Create(
        Guid id,
        Guid orderId,
        Guid itemId,
        ReviewDirection direction,
        Guid authorId,
        Guid subjectId,
        Guid equipmentListingId,
        int rating,
        string? comment,
        DateTimeOffset createdAt,
        DateTimeOffset canEditUntil)
    {
        return new Review(
            id: id,
            orderId: orderId,
            itemId: itemId,
            direction: direction,
            authorId: authorId,
            subjectId: subjectId,
            equipmentListingId: equipmentListingId,
            rating: rating,
            comment: comment,
            authorState: AuthorState.Active,
            moderationState: ModerationState.Visible,
            version: 1,
            createdAt: createdAt,
            updatedAt: null,
            withdrawnAt: null,
            adminHiddenAt: null,
            adminReason: null,
            canEditUntil: canEditUntil);
    }

    /// <summary>
    /// Chỉnh sửa nội dung và điểm đánh giá, tăng version.
    /// </summary>
    public void Update(int rating, string? comment, DateTimeOffset updatedAt)
    {
        Rating = rating;
        Comment = comment;
        UpdatedAt = updatedAt;
        Version++;
    }

    /// <summary>
    /// Tác giả rút lại đánh giá, chuyển trạng thái sang Withdrawn và tăng version.
    /// </summary>
    public void Withdraw(DateTimeOffset withdrawnAt)
    {
        AuthorState = AuthorState.Withdrawn;
        WithdrawnAt = withdrawnAt;
        Version++;
    }

    /// <summary>
    /// Quản trị viên ẩn đánh giá vi phạm tiêu chuẩn, tăng version.
    /// </summary>
    public void AdminHide(string reason, DateTimeOffset hiddenAt)
    {
        ModerationState = ModerationState.Hidden;
        AdminReason = reason;
        AdminHiddenAt = hiddenAt;
        Version++;
    }

    /// <summary>
    /// Quản trị viên khôi phục đánh giá về Visible, tăng version.
    /// </summary>
    public void AdminRestore()
    {
        ModerationState = ModerationState.Visible;
        AdminReason = null;
        AdminHiddenAt = null;
        Version++;
    }

    private static void ValidateRating(int rating)
    {
        if (rating < 1 || rating > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Rating must be between 1 and 5.");
        }
    }

    private static void ValidateAuthorAndSubject(Guid authorId, Guid subjectId)
    {
        if (authorId != Guid.Empty && subjectId != Guid.Empty && authorId == subjectId)
        {
            throw new ArgumentException("Author cannot review themselves (authorId and subjectId must be different).", nameof(authorId));
        }
    }
}
