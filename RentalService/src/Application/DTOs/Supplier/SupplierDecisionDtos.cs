using System.ComponentModel.DataAnnotations;

namespace Dprental.RentalService.Application.DTOs.Supplier;

public sealed record SupplierRejectRequest(
    [Required]
    [MaxLength(500)]
    string RejectReason,

    [MaxLength(2000)]
    string? OptionalNote = null);

public sealed record SupplierDecisionResponse(
    Guid RentalItemId,
    Guid RentalOrderId,
    string OrderCode,
    string Status,
    DateTimeOffset? DecisionAt,
    Guid? DecisionBy,
    string? DecisionReason,
    string? DecisionNote);
