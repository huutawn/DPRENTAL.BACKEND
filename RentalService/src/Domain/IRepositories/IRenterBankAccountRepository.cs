using Dprental.RentalService.Domain.Entities;

namespace Dprental.RentalService.Domain.IRepositories;

/// <summary>
/// Sổ tài khoản ngân hàng của người thuê. Mỗi người có ít tài khoản (giới hạn do Application đặt) nên đọc cả danh sách, không phân trang.
/// </summary>
public interface IRenterBankAccountRepository
{
    /// <summary>Đọc để hiển thị: không theo dõi thay đổi, tài khoản mặc định đứng đầu rồi tới tài khoản mới nhất.</summary>
    Task<IReadOnlyList<RenterBankAccount>> ListByRenterAsync(Guid renterId, CancellationToken cancellationToken = default);

    /// <summary>Đọc một tài khoản của đúng người thuê, không theo dõi thay đổi. Tài khoản của người khác trả null.</summary>
    Task<RenterBankAccount?> GetByIdAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default);

    /// <summary>Đọc để sửa: EF theo dõi các tài khoản trả về, lưu bằng <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task<IReadOnlyList<RenterBankAccount>> ListForUpdateAsync(Guid renterId, CancellationToken cancellationToken = default);

    void Add(RenterBankAccount bankAccount);

    void Remove(RenterBankAccount bankAccount);
}
