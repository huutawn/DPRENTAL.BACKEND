using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.DTOs.Settlement;

public sealed record CompleteManualRefundRequest(
    [Required, MaxLength(256)] string ExternalTransferReference,
    [Required, MaxLength(64)] string OperationKey);

public sealed record CompleteManualRefundResponse(
    Guid RequirementId, string RequirementRef, Guid? RentalItemId,
    string? SettlementRef, decimal Amount, string ExternalTransferReference,
    DateTimeOffset CompletedAt, Guid CompletedBy, bool IsReplay);

public sealed record ManualRefundItemDto(Guid RentalItemId, decimal RentalAmount, decimal DepositAmount);
public sealed record ManualRefundDetails(Guid Id, string RequirementRef, string OperationKey, string Kind,
    string Status, Guid InvoiceId, string PaymentRef, decimal Amount, string Reason,
    IReadOnlyList<ManualRefundItemDto> Items, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    string? ExternalTransferReference);
public sealed record ManualRefundPage(IReadOnlyList<ManualRefundDetails> Items, Guid? NextCursor);
