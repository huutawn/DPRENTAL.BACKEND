using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Dprental.PaymentService.Application.DTOs.Payout;

public sealed record RegisterPayoutDestinationRequest(
    [Required, MaxLength(64)] string OperationKey,
    [MaxLength(64)] string? DestinationRef,
    [Required, MaxLength(32)] string BankCode,
    [Required, MaxLength(128)] string AccountHolderName,
    [Required, MaxLength(32)] string AccountNumber,
    [Required, MaxLength(128)] string VerificationRef,
    [Required, MaxLength(500)] string AuditReason)
{

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("OperationKey = ").Append(OperationKey)
               .Append(", DestinationRef = ").Append(DestinationRef)
               .Append(", BankCode = ").Append(BankCode)
               .Append(", AccountNumber = ***");
        return true;
    }
}

public sealed record PayoutDestinationResponse(
    Guid Id, Guid SupplierId, string DestinationRef, int Version, string BankCode,
    string AccountHolderName, string AccountNumberMasked, string VerificationRef,
    DateTimeOffset VerifiedAt, Guid VerifiedBy, bool IsReplay);
