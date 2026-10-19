using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.RentalService.Common.Utils;

namespace Dprental.RentalService.Application.Kafka.Events;

/// <summary>
/// Published to rental.events.v1 when a RentalOrder is created in PENDING_PAYMENT state.
/// Intended for Notification and Reporting services.
/// Excludes physical identity (AssetId, SerialNumber) and personal delivery/contact snapshots.
/// </summary>
public sealed record RentalOrderCreatedPayload(
    [Required] Guid RentalOrderId,
    [Required] string OrderCode,
    [Required] Guid RenterId,
    [Required] string Currency,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal TotalRentalAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal TotalDepositAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal TotalPayableAmount,
    [Required] DateTimeOffset PaymentDeadline,
    [Required] DateTimeOffset CreatedAt,
    [Required] IReadOnlyList<RentalOrderCreatedItemPayload> Items);

public sealed record RentalOrderCreatedItemPayload(
    [Required] Guid RentalItemId,
    [Required] Guid EquipmentListingId,
    [Required] Guid SupplierId,
    [Range(1, 100)] int Quantity,
    [Required] DateOnly ExpectedStartDate,
    [Required] DateOnly ExpectedEndDate,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RentalAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal DepositAmount);

public sealed record RentalSupplierConfirmationRequestedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    Guid EquipmentListingId,
    int Quantity,
    DateTimeOffset SupplierConfirmationDeadline);

public sealed record RentalItemConfirmedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset DecisionAt,
    string InventoryConfirmationRef);

public sealed record RentalItemRejectedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset DecisionAt,
    string Reason,
    string? OptionalNote);

public sealed record RentalItemExpiredPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset ExpiredAt,
    string Reason);

public sealed record RentalOrderPaidPayload(
    Guid RentalOrderId,
    Guid RenterId,
    string InvoiceRef,
    string PaymentRef,
    decimal PaidAmount,
    DateTimeOffset PaidAt);

public sealed record RentalOrderExpiredPayload(
    Guid RentalOrderId,
    Guid RenterId,
    DateTimeOffset ExpiredAt,
    string Reason);

public sealed record LatePaymentRefundRequestedPayload(
    Guid RentalOrderId,
    Guid RenterId,
    string InvoiceRef,
    string PaymentRef,
    decimal TotalRefundAmount,
    DateTimeOffset RequestedAt);

public sealed record DeadLetterPayload(
    string OriginalMessageType,
    string OriginalPayload,
    string Reason,
    DateTimeOffset FailedAt);

public sealed record CartItemAddedPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("cartId")] Guid CartId,
    [property: JsonPropertyName("cartItemId")] Guid CartItemId);

public sealed record RentalItemPaidPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId);

public sealed record RentalItemCompletedPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId);
