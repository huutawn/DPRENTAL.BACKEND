using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_operation_receipts", Schema = "rental")]
[Index(nameof(RentalOrderId), nameof(OperationId), IsUnique = true)]
public class RentalOperationReceipt
{
    [Key]
    public Guid Id { get; private set; }

    public Guid RentalOrderId { get; private set; }

    [Required]
    [MaxLength(64)]
    public string OperationId { get; private set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string Action { get; private set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string Fingerprint { get; private set; } = string.Empty;

    public bool IsSuccess { get; private set; }

    [MaxLength(64)]
    public string? ErrorCode { get; private set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    [ForeignKey(nameof(RentalOrderId))]
    public RentalOrder? RentalOrder { get; private set; }

    private RentalOperationReceipt()
    {
    }

    internal RentalOperationReceipt(
        Guid id,
        Guid rentalOrderId,
        string operationId,
        string action,
        string fingerprint,
        bool isSuccess,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset createdAt)
    {
        Id = id;
        RentalOrderId = rentalOrderId;
        OperationId = operationId;
        Action = action;
        Fingerprint = fingerprint;
        IsSuccess = isSuccess;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
    }
}
