using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.DTOs.Payout;

/// <summary>Trạng thái chuyển tiền cho supplier; độc lập với trạng thái RentalSettled.</summary>
public static class SupplierPayoutStatus
{
    public const string PendingPayout = "PENDING_PAYOUT";
    public const string Completed = "COMPLETED";
}

/// <summary>Bản ghi phải trả cho đúng một supplier và một RentalItem.</summary>
public sealed record SupplierPayoutResponse(
    Guid PayoutId,
    string PayoutRef,
    Guid SettlementId,
    Guid RentalItemId,
    Guid SupplierId,
    decimal Amount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ExternalTransferReference);

/// <summary>Admin xác nhận đã chuyển khoản thực tế ngoài hệ thống.</summary>
public sealed record CompleteSupplierPayoutRequest(
    [Required, MaxLength(64)] string OperationKey,
    [Required, MaxLength(256)] string ExternalTransferReference,
    [Required, MaxLength(500)] string AuditReason);

public sealed record CompleteSupplierPayoutResponse(
    Guid PayoutId,
    string PayoutRef,
    string Status,
    string OperationKey,
    string ExternalTransferReference,
    DateTimeOffset CompletedAt,
    Guid CompletedBy,
    bool IsReplay);

