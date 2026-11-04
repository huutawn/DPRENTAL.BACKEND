using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Payments;

public sealed class TransferRecipientResolverTests
{
    private static readonly Guid SupplierId = Guid.NewGuid();

    [Fact]
    public async Task Supplier_account_is_the_default_one()
    {
        var now = DateTimeOffset.UtcNow;
        var repo = new StubBankAccounts(
            new RenterBankAccount(Guid.NewGuid(), SupplierId, "VCB", "1111222233", "CONG TY MAY XAY DUNG MINH TAM", false, now),
            new RenterBankAccount(Guid.NewGuid(), SupplierId, "MB", "0123456789", "CONG TY MAY XAY DUNG MINH TAM", true, now));

        var account = await TransferRecipientResolver.FindSupplierAccountAsync(repo, SupplierId, CancellationToken.None);

        Assert.Equal(new TransferRecipientDto("MB", "0123456789", "CONG TY MAY XAY DUNG MINH TAM"), account);
    }

    [Fact]
    public async Task Supplier_without_default_account_sends_no_account()
    {
        Assert.Null(await TransferRecipientResolver.FindSupplierAccountAsync(new StubBankAccounts(), SupplierId, CancellationToken.None));
        Assert.Null(await TransferRecipientResolver.FindSupplierAccountAsync(null, SupplierId, CancellationToken.None));
    }

    [Fact]
    public void Refund_account_comes_from_order_refund_destination()
    {
        var account = TransferRecipientResolver.RefundAccountOf(
            """{"bankCode":"VCB","accountNumber":"9988776655","accountHolderName":"NGUYEN VAN TUAN"}""");

        Assert.Equal(new TransferRecipientDto("VCB", "9988776655", "NGUYEN VAN TUAN"), account);
        Assert.Null(TransferRecipientResolver.RefundAccountOf("ORIGINAL_PAYMENT_SOURCE"));
    }

    private sealed class StubBankAccounts(params RenterBankAccount[] accounts) : IRenterBankAccountRepository
    {
        public Task<IReadOnlyList<RenterBankAccount>> ListByRenterAsync(Guid renterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RenterBankAccount>>(accounts.Where(a => a.RenterId == renterId).ToList());

        public Task<RenterBankAccount?> GetByIdAsync(Guid renterId, Guid bankAccountId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RenterBankAccount>> ListForUpdateAsync(Guid renterId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Add(RenterBankAccount bankAccount) => throw new NotSupportedException();

        public void Remove(RenterBankAccount bankAccount) => throw new NotSupportedException();
    }
}
