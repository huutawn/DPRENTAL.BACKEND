using Dprental.PaymentService.Common;
namespace Dprental.PaymentService.Domain.IRepository;

public sealed record RegisterPayoutDestinationCommand(
    Guid SupplierId, Guid AdminId, string OperationKey, string? DestinationRef,
    string BankCode, string AccountHolderName, string AccountNumberLast4, string AccountFingerprint,
    string VerificationRef, string AuditReason);

public sealed record PayoutDestinationRow(
        Guid Id, Guid SupplierId, string DestinationRef, int Version, string BankCode, string AccountHolderName,
        string AccountNumberLast4, string VerificationRef, DateTimeOffset VerifiedAt, Guid VerifiedBy, bool IsReplay);

public interface ISupplierPayoutDestinationRepository
{
    Task<Result<PayoutDestinationRow>> RegisterAsync(RegisterPayoutDestinationCommand command, CancellationToken ct);
    Task<IReadOnlyList<PayoutDestinationRow>> ListBySupplierAsync(Guid supplierId, CancellationToken ct);
}
