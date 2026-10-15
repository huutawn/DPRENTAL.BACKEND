using System.ComponentModel.DataAnnotations;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;

namespace Dprental.RentalService.Application.Kafka.Contracts;

// PROPOSED command contracts (Not yet confirmed by downstream services Inventory/Payment).
// Reuses existing HTTP DTOs as request payload data without duplicating schemas.


/// <summary>
/// PROPOSED: Command sent to payment.commands.v1 to create a single invoice for the order.
/// Partition Key: RentalOrderId
/// </summary>
public sealed record CreateInvoiceCommandPayload(
    [Required] Guid RentalOrderId,
    [Required] string OperationKey,
    [Required] CreateInvoiceRequest Request);
