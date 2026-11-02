using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.BankAccounts;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Application.Services.BankAccounts;

/// <summary>
/// Admin xem tài khoản mặc định của nhà cung cấp để chuyển tiền quyết toán. Kết quả có đủ số tài khoản nên mỗi lần xem đều được ghi log.
/// </summary>
public sealed partial class AdminSupplierBankAccountService(
    IRenterBankAccountRepository bankAccounts,
    ILogger<AdminSupplierBankAccountService> logger) : IAdminSupplierBankAccountService
{
    public async Task<Result<AdminSupplierBankAccountResponse>> GetDefaultAsync(
        Guid supplierId,
        IRentalCallerContext caller,
        CancellationToken cancellationToken = default)
    {
        Result access = RentalAuthorizationPolicy.AuthorizeAdminSupplierBankAccountRead(caller);
        if (access.IsFailure)
        {
            return access.Error;
        }

        IReadOnlyList<RenterBankAccount> accounts = await bankAccounts.ListByRenterAsync(supplierId, cancellationToken);
        RenterBankAccount? account = accounts.FirstOrDefault(a => a.IsDefault);

        LogAdminViewedSupplierBankAccount(logger, caller.UserId, supplierId, account is not null);

        return account is null
            ? RenterBankAccountErrors.NotFound
            : new AdminSupplierBankAccountResponse(account.BankCode, account.AccountNumber, account.AccountHolderName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {AdminId} viewed bank account of supplier {SupplierId} (found {Found})")]
    private static partial void LogAdminViewedSupplierBankAccount(ILogger logger, Guid? adminId, Guid supplierId, bool found);
}
