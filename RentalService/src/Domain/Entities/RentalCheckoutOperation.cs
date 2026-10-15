using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.RentalService.Common.Enums;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_checkout_operations", Schema = "rental")]
[Index(nameof(RenterUserId))]
[Index(nameof(RenterUserId), nameof(Status))]
public class RentalCheckoutOperation
{
    [Key]
    [MaxLength(128)]
    public string OperationId { get; private set; } = string.Empty;

    public Guid RenterUserId { get; private set; }

    [Required]
    [MaxLength(64)]
    public string RequestFingerprint { get; private set; } = string.Empty;

    public Guid ExpectedRentalOrderId { get; private set; }

    public DateTimeOffset HoldExpiresAt { get; private set; }

    public CheckoutOperationStatus Status { get; private set; }

    [Required]
    public string PreparationSnapshotJson { get; private set; } = string.Empty;

    [Required]
    public string ItemHoldStatusesJson { get; private set; } = string.Empty;

    [MaxLength(100)]
    public string? ErrorCode { get; private set; }

    [MaxLength(500)]
    public string? ErrorMessage { get; private set; }

    [MaxLength(50)]
    public string? InvoiceStatus { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private RentalCheckoutOperation()
    {
    }

    public RentalCheckoutOperation(
        string operationId,
        Guid renterUserId,
        string requestFingerprint,
        Guid expectedRentalOrderId,
        DateTimeOffset holdExpiresAt,
        string preparationSnapshotJson,
        string itemHoldStatusesJson,
        DateTimeOffset createdAt)
    {
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        RenterUserId = renterUserId;
        RequestFingerprint = requestFingerprint ?? throw new ArgumentNullException(nameof(requestFingerprint));
        ExpectedRentalOrderId = expectedRentalOrderId;
        HoldExpiresAt = holdExpiresAt;
        PreparationSnapshotJson = preparationSnapshotJson ?? throw new ArgumentNullException(nameof(preparationSnapshotJson));
        ItemHoldStatusesJson = itemHoldStatusesJson ?? throw new ArgumentNullException(nameof(itemHoldStatusesJson));
        Status = CheckoutOperationStatus.Processing;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public void MarkSucceeded(DateTimeOffset updatedAt, string? invoiceStatus = null)
    {
        Status = CheckoutOperationStatus.Succeeded;
        UpdatedAt = updatedAt;
        InvoiceStatus = invoiceStatus;
    }

    public void MarkFailed(string errorCode, string errorMessage, DateTimeOffset updatedAt)
    {
        Status = CheckoutOperationStatus.Failed;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        UpdatedAt = updatedAt;
    }

    public void MarkTimedOut(DateTimeOffset updatedAt)
    {
        Status = CheckoutOperationStatus.TimedOut;
        ErrorCode = "CHECKOUT_HOLD_TIMEOUT";
        ErrorMessage = "Quá hạn 30 phút giữ chỗ chưa nhận đủ kết quả; tác vụ đã bị hủy.";
        UpdatedAt = updatedAt;
    }

    public void UpdateItemHoldStatuses(string itemHoldStatusesJson, DateTimeOffset updatedAt)
    {
        ItemHoldStatusesJson = itemHoldStatusesJson ?? throw new ArgumentNullException(nameof(itemHoldStatusesJson));
        UpdatedAt = updatedAt;
    }

    public void SetInvoiceStatus(string invoiceStatus, DateTimeOffset updatedAt)
    {
        InvoiceStatus = invoiceStatus;
        UpdatedAt = updatedAt;
    }
}
