using System.ComponentModel.DataAnnotations;

namespace Dprental.PaymentService.Application.DTOs.Operations;

/// <summary>Tham số danh sách vận hành; page bắt đầu từ 1, pageSize tối đa 100.</summary>
public sealed record FinancialListQuery(
    [Range(1, int.MaxValue)] int Page = 1,
    [Range(1, 100)] int PageSize = 20,
    string? Status = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record FinancialPage<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>Thông tin chung của một thao tác thủ công; actor và timestamp lấy từ server.</summary>
public sealed record FinancialOperationAuditResponse(
    Guid OperationId,
    string OperationKey,
    string OperationType,
    Guid ActorId,
    Guid TargetId,
    string ExternalReference,
    string AuditReason,
    DateTimeOffset OccurredAt);

/// <summary>Admin đối soát một giao dịch unmatched với hóa đơn đã xác minh.</summary>
public sealed record ResolveUnmatchedTransactionRequest(
    [Required, MaxLength(64)] string InvoiceRef,
    [Required, MaxLength(64)] string OperationKey,
    [Required, MaxLength(256)] string ExternalReference,
    [Required, MaxLength(500)] string AuditReason);

public sealed record ResolveUnmatchedTransactionResponse(
    Guid ResolutionId, Guid TransactionId, string InvoiceRef, string PaymentRef,
    DateTimeOffset ResolvedAt, Guid ResolvedBy, bool IsReplay);
