using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.IServices.BankAccounts;

public interface IAdminSupplierBankAccountService
{
    Task<Result<AdminSupplierBankAccountResponse>> GetDefaultAsync(
        Guid supplierId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default);
}
