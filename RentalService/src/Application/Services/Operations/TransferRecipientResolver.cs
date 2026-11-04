using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Services.Orders;
using Dprental.RentalService.Domain.IRepositories;

namespace Dprental.RentalService.Application.Services.Operations;

/// <summary>
/// Tài khoản nhận tiền gửi kèm request sang Payment để Payment ghép QR cho Admin.
/// Đọc lúc gửi để lấy tài khoản mới nhất; thiếu tài khoản thì trả null, khoản tiền vẫn tạo được nhưng không có QR.
/// </summary>
internal static class TransferRecipientResolver
{
    // supplierId là id người đại diện của nhà cung cấp, cũng là chủ sổ tài khoản trong Rental.
    public static async Task<TransferRecipientDto?> FindSupplierAccountAsync(
        IRenterBankAccountRepository? bankAccounts, Guid supplierId, CancellationToken ct)
    {
        if (bankAccounts is null) return null;
        var account = (await bankAccounts.ListByRenterAsync(supplierId, ct)).FirstOrDefault(a => a.IsDefault);
        return account is null ? null : new TransferRecipientDto(account.BankCode, account.AccountNumber, account.AccountHolderName);
    }

    public static TransferRecipientDto? RefundAccountOf(string refundDestinationRef) =>
        OrderQueryMapper.ParseRefundAccount(refundDestinationRef) is { } a
            ? new TransferRecipientDto(a.BankCode, a.AccountNumber, a.AccountHolderName)
            : null;
}
