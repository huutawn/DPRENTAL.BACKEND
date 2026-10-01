using Dprental.InventoryService.Domain.Entities;

namespace Dprental.InventoryService.Domain.IRepositories;

/// <summary>
/// Hợp đồng repository quản lý aggregate Reservation và các chuyển đổi trạng thái liên quan.
/// Không trả về IQueryable hay DbContext types để bảo vệ tính đóng gói của domain model.
/// </summary>
public interface IReservationRepository : IRepo<Reservation, Guid>
{
    /// <summary>
    /// Tìm kiếm lượt đặt lịch theo ID và giới hạn trong phạm vi tổ chức sở hữu thiết bị.
    /// </summary>
    /// <param name="id">Định danh lượt đặt lịch.</param>
    /// <param name="organizationId">Định danh tổ chức sở hữu thiết bị.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Đối tượng Reservation nếu tìm thấy và thuộc tổ chức, ngược lại null.</returns>
    Task<Reservation?> GetByIdForOrganizationAsync(Guid id, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tìm kiếm lượt đặt lịch theo hệ thống nguồn và mã tham chiếu external (Idempotency lookup).
    /// </summary>
    /// <param name="sourceSystem">Tên hệ thống nguồn tạo đơn.</param>
    /// <param name="externalReference">Mã tham chiếu external từ hệ thống ngoài.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Reservation tương ứng nếu đã tồn tại, ngược lại null.</returns>
    Task<Reservation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các reservation ứng viên có khả năng chiếm lịch (Held, Confirmed, InUse, Completed) trên thiết bị.
    /// Bao gồm cả các lượt Completed mà ngày kết thúc/thu hồi D có thể chặn đặt lại cho đến D+1.
    /// </summary>
    /// <param name="equipmentId">Định danh thiết bị.</param>
    /// <param name="startDate">Ngày bắt đầu khoảng truy vấn.</param>
    /// <param name="endDate">Ngày kết thúc khoảng truy vấn.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các reservation ứng viên cần đưa vào availability policy.</returns>
    Task<IReadOnlyList<Reservation>> GetActiveOrCompletedCandidatesAsync(
        Guid equipmentId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các lượt giữ chỗ (Held) đã quá hạn tại thời điểm kiểm tra trên thiết bị.
    /// </summary>
    /// <param name="equipmentId">Định danh thiết bị.</param>
    /// <param name="now">Thời điểm hiện tại (UTC).</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các lượt giữ chỗ quá hạn cần xử lý giải phóng.</returns>
    Task<IReadOnlyList<Reservation>> GetExpiredHoldsAsync(
        Guid equipmentId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách các lượt đặt lịch của một khách thuê có phân trang.
    /// </summary>
    /// <param name="renterAccountId">Định danh tài khoản khách thuê.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các lượt đặt lịch của khách thuê.</returns>
    Task<IReadOnlyList<Reservation>> ListByRenterAsync(
        Guid renterAccountId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lấy danh sách toàn bộ các lượt đặt lịch của các thiết bị thuộc một tổ chức có phân trang.
    /// </summary>
    /// <param name="organizationId">Định danh tổ chức.</param>
    /// <param name="skip">Số bản ghi bỏ qua.</param>
    /// <param name="take">Số bản ghi cần lấy.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Danh sách các lượt đặt lịch thuộc tổ chức.</returns>
    Task<IReadOnlyList<Reservation>> ListByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Kiểm tra khách thuê hiện tại có đang trong quá trình thuê hoạt động (InUse) hay không.
    /// </summary>
    /// <param name="renterAccountId">Định danh khách thuê.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>True nếu khách thuê đang có thiết bị đang thuê (InUse), ngược lại False.</returns>
    Task<bool> HasActiveRentalsForRenterAsync(Guid renterAccountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đếm số báo hỏng của một lượt đặt.
    /// </summary>
    /// <param name="reservationId">Định danh lượt đặt.</param>
    /// <param name="cancellationToken">Token hủy thao tác.</param>
    /// <returns>Số báo hỏng trên lượt đặt.</returns>
    Task<int> GetDamageReportsCountForReservationAsync(Guid reservationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Đưa lượt đặt lịch mới vào hàng đợi lưu trữ (stage changes).
    /// </summary>
    /// <param name="reservation">Đối tượng reservation cần thêm.</param>
    void Add(Reservation reservation);

    /// <summary>
    /// Đánh dấu cập nhật lượt đặt lịch (stage changes).
    /// </summary>
    /// <param name="reservation">Đối tượng reservation cần cập nhật.</param>
    void Update(Reservation reservation);
}
