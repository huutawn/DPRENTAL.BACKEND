using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Sổ địa chỉ giao của người thuê. Mỗi người có ít địa chỉ (giới hạn do Application đặt) nên đọc cả danh sách, không phân trang.
/// </summary>
public interface IRenterAddressRepository
{
    /// <summary>Đọc để hiển thị: không theo dõi thay đổi, địa chỉ mặc định đứng đầu rồi tới địa chỉ mới nhất.</summary>
    Task<IReadOnlyList<RenterAddress>> ListByRenterAsync(Guid renterId, CancellationToken cancellationToken = default);

    /// <summary>Đọc để sửa: EF theo dõi các địa chỉ trả về, lưu bằng <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task<IReadOnlyList<RenterAddress>> ListForUpdateAsync(Guid renterId, CancellationToken cancellationToken = default);

    void Add(RenterAddress address);

    void Remove(RenterAddress address);
}
