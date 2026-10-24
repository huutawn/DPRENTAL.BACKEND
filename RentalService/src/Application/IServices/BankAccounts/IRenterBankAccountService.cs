using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.BankAccounts;

/// <summary>
/// Sổ tài khoản ngân hàng nhận hoàn tiền của người thuê. Mọi thao tác chỉ chạm tới tài khoản của chính người thuê đó.
/// </summary>
public interface IRenterBankAccountService
{
    Task<IReadOnlyList<RenterBankAccountResponse>> ListAsync(Guid renterId, CancellationToken cancellationToken = default);

    Task<Result<RenterBankAccountResponse>> GetAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default);

    Task<Result<RenterBankAccountResponse>> CreateAsync(Guid renterId, CreateRenterBankAccountRequest request, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default);

    Task<Result<RenterBankAccountResponse>> SetDefaultAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default);
}
