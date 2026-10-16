using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.DTOs.Settlement;

public sealed record CompleteManualRefundRequest(
    [Required, MaxLength(256)] string ExternalTransferReference);

public sealed record CompleteManualRefundResponse(
    Guid RequirementId, string RequirementRef, Guid RentalItemId,
    string SettlementRef, decimal Amount, string ExternalTransferReference,
    DateTimeOffset CompletedAt, Guid CompletedBy, bool IsReplay);
