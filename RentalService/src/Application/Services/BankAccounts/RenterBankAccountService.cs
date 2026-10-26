using Dprental.RentalService.Application.DTOs.BankAccounts;
using Dprental.RentalService.Application.DTOs.Checkout;
using Dprental.RentalService.Application.Errors;
using Dprental.RentalService.Application.IServices.BankAccounts;
using Dprental.RentalService.Application.Validation;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.BankAccounts;

/// <summary>
/// Sổ tài khoản ngân hàng của người thuê: tối đa <see cref="MaxBankAccounts"/> tài khoản, không trùng ngân hàng và số tài khoản,
/// luôn có đúng một tài khoản mặc định khi sổ không rỗng.
/// Mọi thao tác sửa đổi khóa theo người thuê để hai yêu cầu cùng lúc không làm sổ có hai tài khoản mặc định hay hai tài khoản trùng.
/// </summary>
public sealed class RenterBankAccountService(
    IRenterBankAccountRepository bankAccounts,
    IOrderLockManager locks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRenterBankAccountService
{
    public const int MaxBankAccounts = 10;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<RenterBankAccountResponse>> ListAsync(Guid renterId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RenterBankAccount> list = await bankAccounts.ListByRenterAsync(renterId, cancellationToken);
        return list.Select(ToResponse).ToList();
    }

    public async Task<Result<RenterBankAccountResponse>> GetAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        RenterBankAccount? bankAccount = await bankAccounts.GetByIdAsync(renterId, bankAccountId, cancellationToken);
        return bankAccount is null ? RenterBankAccountErrors.NotFound : ToResponse(bankAccount);
    }

    public async Task<Result<RenterBankAccountResponse>> CreateAsync(
        Guid renterId,
        CreateRenterBankAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is { } invalid) return invalid;

        string bankCode = request.BankCode!.Trim();
        string accountNumber = request.AccountNumber!.Trim();

        return await WithRenterLockAsync<RenterBankAccountResponse>(renterId, async () =>
        {
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            if (existing.Any(a => a.BankCode == bankCode && a.AccountNumber == accountNumber))
            {
                return RenterBankAccountErrors.Duplicate;
            }

            if (existing.Count >= MaxBankAccounts) return RenterBankAccountErrors.LimitReached;

            // Tài khoản đầu tiên tự thành mặc định.
            RenterBankAccount bankAccount = new(
                Guid.NewGuid(),
                renterId,
                bankCode,
                accountNumber,
                request.AccountHolderName!.Trim(),
                isDefault: existing.Count == 0,
                timeProvider.GetUtcNow());
            bankAccounts.Add(bankAccount);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(bankAccount);
        }, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        Result<bool> result = await WithRenterLockAsync<bool>(renterId, async () =>
        {
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            RenterBankAccount? bankAccount = existing.FirstOrDefault(a => a.Id == bankAccountId);
            if (bankAccount is null) return RenterBankAccountErrors.NotFound;

            bankAccounts.Remove(bankAccount);
            if (bankAccount.IsDefault)
            {
                // Xóa tài khoản mặc định thì tài khoản mới nhất còn lại thành mặc định (danh sách đã xếp mới nhất trước).
                existing.FirstOrDefault(a => a.Id != bankAccountId)?.SetDefault(true, timeProvider.GetUtcNow());
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();
    }

    public async Task<Result<RenterBankAccountResponse>> SetDefaultAsync(
        Guid renterId,
        Guid bankAccountId,
        CancellationToken cancellationToken = default)
    {
        return await WithRenterLockAsync<RenterBankAccountResponse>(renterId, async () =>
        {
            IReadOnlyList<RenterBankAccount> existing = await bankAccounts.ListForUpdateAsync(renterId, cancellationToken);
            RenterBankAccount? target = existing.FirstOrDefault(a => a.Id == bankAccountId);
            if (target is null) return RenterBankAccountErrors.NotFound;

            DateTimeOffset utcNow = timeProvider.GetUtcNow();
            foreach (RenterBankAccount bankAccount in existing)
            {
                bankAccount.SetDefault(bankAccount.Id == bankAccountId, utcNow);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResponse(target);
        }, cancellationToken);
    }

    private static Error? Validate(CreateRenterBankAccountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CheckoutFieldError> errors = [];
        CheckoutRequestValidator.ValidateBankAccount(
            request.BankCode, request.AccountNumber, request.AccountHolderName, string.Empty, errors);
        return errors.Count == 0
            ? null
            : RenterBankAccountErrors.InvalidRequest(string.Join(" ", errors.Select(e => e.Message)));
    }

    private async Task<Result<T>> WithRenterLockAsync<T>(
        Guid renterId,
        Func<Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable renterLock;
        try
        {
            renterLock = await locks.AcquireRenterLockAsync(renterId, LockTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return RenterBankAccountErrors.Busy;
        }

        await using (renterLock)
        {
            return await action();
        }
    }

    private static RenterBankAccountResponse ToResponse(RenterBankAccount bankAccount) => new(
        bankAccount.Id,
        bankAccount.BankCode,
        bankAccount.AccountNumber,
        bankAccount.AccountHolderName,
        bankAccount.IsDefault);
}
