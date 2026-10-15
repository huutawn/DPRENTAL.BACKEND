using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Dprental.InventoryService.Common.Enums;

namespace Dprental.InventoryService.Domain.Entities;

[Table("allocation_operation_receipts", Schema = "inventory")]
public sealed class AllocationOperationReceipt
{
    [Key]
    [Column("id")]
    public Guid Id { get; private set; }

    [Required]
    [Column("source_system")]
    [MaxLength(100)]
    public string SourceSystem { get; private set; } = string.Empty;

    [Required]
    [Column("operation_key")]
    [MaxLength(100)]
    public string OperationKey { get; private set; } = string.Empty;

    [Column("rental_order_id")]
    public Guid RentalOrderId { get; private set; }

    [Column("group_reference")]
    [MaxLength(100)]
    public string? GroupReference { get; private set; }

    [Required]
    [Column("command_type")]
    [MaxLength(50)]
    public string CommandType { get; private set; } = string.Empty;

    [Required]
    [Column("payload_fingerprint")]
    [MaxLength(64)]
    public string PayloadFingerprint { get; private set; } = string.Empty;

    [Column("status")]
    public AllocationReceiptStatus Status { get; private set; }

    [Column("closes_group")]
    public bool ClosesGroup { get; private set; }

    [Column("error_code")]
    [MaxLength(100)]
    public string? ErrorCode { get; private set; }

    [Column("error_message")]
    [MaxLength(500)]
    public string? ErrorMessage { get; private set; }

    [Column("result_payload_json")]
    public string? ResultPayloadJson { get; private set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; private set; }

    private AllocationOperationReceipt()
    {
    }

    public static AllocationOperationReceipt CreateSucceeded(
        Guid id,
        string sourceSystem,
        string operationKey,
        Guid rentalOrderId,
        string? groupReference,
        string commandType,
        string payloadFingerprint,
        bool closesGroup,
        string? resultPayloadJson,
        DateTimeOffset createdAt)
    {
        return new AllocationOperationReceipt
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            SourceSystem = sourceSystem.Trim(),
            OperationKey = operationKey.Trim(),
            RentalOrderId = rentalOrderId,
            GroupReference = groupReference?.Trim(),
            CommandType = commandType.Trim(),
            PayloadFingerprint = payloadFingerprint.Trim(),
            Status = AllocationReceiptStatus.Succeeded,
            ClosesGroup = closesGroup,
            ResultPayloadJson = resultPayloadJson,
            CreatedAt = createdAt
        };
    }

    public static AllocationOperationReceipt CreateRejected(
        Guid id,
        string sourceSystem,
        string operationKey,
        Guid rentalOrderId,
        string? groupReference,
        string commandType,
        string payloadFingerprint,
        bool closesGroup,
        string errorCode,
        string errorMessage,
        DateTimeOffset createdAt)
    {
        return new AllocationOperationReceipt
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            SourceSystem = sourceSystem.Trim(),
            OperationKey = operationKey.Trim(),
            RentalOrderId = rentalOrderId,
            GroupReference = groupReference?.Trim(),
            CommandType = commandType.Trim(),
            PayloadFingerprint = payloadFingerprint.Trim(),
            Status = AllocationReceiptStatus.Rejected,
            ClosesGroup = closesGroup,
            ErrorCode = errorCode?.Trim(),
            ErrorMessage = errorMessage?.Trim(),
            CreatedAt = createdAt
        };
    }
}
