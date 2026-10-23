using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Dprental.RentalService.Common.Utils;
using Dprental.RentalService.Application.Kafka.Contracts;

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
    [Required] IReadOnlyList<RentalOrderCreatedItemPayload> Items,
    long OrderVersion = 1);

public sealed record RentalOrderCreatedItemPayload(
    [Required] Guid RentalItemId,
    [Required] Guid EquipmentListingId,
    [Required] Guid SupplierId,
    [Range(1, 100)] int Quantity,
    [Required] DateOnly ExpectedStartDate,
    [Required] DateOnly ExpectedEndDate,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal RentalAmount,
    [Range(0, (double)VndAmountValidator.MaxVndAmount)] decimal DepositAmount,
    Guid? EquipmentTypeId = null,
    string? EquipmentTypeName = null,
    Guid? BrandId = null,
    string? BrandName = null,
    string? Model = null);

public sealed record RentalSupplierConfirmationRequestedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    Guid EquipmentListingId,
    int Quantity,
    DateTimeOffset SupplierConfirmationDeadline,
    long OrderVersion = 1);

public sealed record RentalItemConfirmedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset DecisionAt,
    string InventoryConfirmationRef,
    long OrderVersion = 1);

public sealed record RentalItemRejectedPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset DecisionAt,
    string Reason,
    string? OptionalNote,
    long OrderVersion = 1);

public sealed record RentalItemExpiredPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    DateTimeOffset ExpiredAt,
    string Reason,
    long OrderVersion = 1);

public sealed record RentalOrderPaidPayload(
    Guid RentalOrderId,
    Guid RenterId,
    string InvoiceRef,
    string PaymentRef,
    decimal PaidAmount,
    DateTimeOffset PaidAt,
    long OrderVersion = 1);

public sealed record RentalOrderExpiredPayload(
    Guid RentalOrderId,
    Guid RenterId,
    DateTimeOffset ExpiredAt,
    string Reason,
    long OrderVersion = 1);

public sealed record LatePaymentRefundRequestedPayload(
    Guid RentalOrderId,
    Guid RenterId,
    string InvoiceRef,
    string PaymentRef,
    decimal TotalRefundAmount,
    DateTimeOffset RequestedAt,
    long? OrderVersion = null);

public sealed record DeadLetterPayload(
    string OriginalMessageType,
    string OriginalPayload,
    string Reason,
    DateTimeOffset FailedAt);

public sealed record RentalItemCancelledPayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid ActorId,
    string Role,
    DateTimeOffset CancelledAt,
    string Reason,
    decimal RefundAmount,
    long OrderVersion = 1);

public sealed record RentalHandoverOverduePayload(
    Guid RentalOrderId,
    Guid RentalItemId,
    Guid SupplierId,
    Guid RenterId,
    DateOnly ExpectedStartDate,
    DateTimeOffset OverdueAt,
    long OrderVersion = 1);

public sealed record CartItemAddedPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("equipmentTypeId")] Guid EquipmentTypeId,
    [property: JsonPropertyName("cartId")] Guid CartId,
    [property: JsonPropertyName("cartItemId")] Guid CartItemId);

public sealed record RentalItemPaidPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("equipmentTypeId")] Guid? EquipmentTypeId,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId,
    [property: JsonPropertyName("orderVersion")] long OrderVersion = 1);

public sealed record RentalItemCompletedPayload(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("equipmentListingId")] Guid EquipmentListingId,
    [property: JsonPropertyName("equipmentTypeId")] Guid? EquipmentTypeId,
    [property: JsonPropertyName("rentalOrderId")] Guid RentalOrderId,
    [property: JsonPropertyName("rentalItemId")] Guid RentalItemId,
    [property: JsonPropertyName("orderVersion")] long OrderVersion = 1);

public sealed record RentalItemHandoverCompletedPayload(
    Guid RentalOrderId, Guid RentalItemId, Guid RenterId, Guid SupplierId,
    Guid EquipmentListingId, int Quantity, Guid HandoverRecordId,
    DateTimeOffset ActualHandoverAt, DateTimeOffset CompletedAt,
    long OrderVersion = 1);

public sealed record RentalItemReturnedPayload(
    Guid RentalOrderId, Guid RentalItemId, Guid RenterId, Guid SupplierId,
    Guid EquipmentListingId, int Quantity, DateTimeOffset ReturnsCompletedAt,
    long OrderVersion = 1);

public sealed record RentalOrderCancelledPayload(
    Guid RentalOrderId, Guid RenterId, DateTimeOffset CancelledAt, string Reason,
    long OrderVersion = 1);

public sealed record RentalOrderCompletedPayload(
    Guid RentalOrderId, Guid RenterId, DateTimeOffset CompletedAt,
    long OrderVersion = 1);
