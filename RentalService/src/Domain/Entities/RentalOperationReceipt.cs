using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Entities;

[Table("rental_operation_receipts", Schema = "rental")]
[Index(nameof(RentalOrderId), nameof(OperationId), IsUnique = true)]
[Index(nameof(Status), nameof(NextRetryAt))]
[Index(nameof(Action), nameof(Status))]
[Index(nameof(TargetItemId))]
[Index(nameof(PrerequisiteOperationId))]
public class RentalOperationReceipt
{
    public const string StatusPending = "Pending";
    public const string StatusProcessing = "Processing";
    public const string StatusSucceeded = "Succeeded";
    public const string StatusFailed = "Failed";
    public const string StatusManualRefundRequired = "MANUAL_REFUND_REQUIRED";
    public const string StatusManualReconciliationRequired = "MANUAL_RECONCILIATION_REQUIRED";
    public const string ClassificationTransient = "Transient";
    public const string ClassificationBusiness = "Business";
    public const string ClassificationUncertain = "Uncertain";
    public const string ClassificationUncertainExhausted = "UncertainExhausted";
    public const string ClassificationTransientExhausted = "TransientExhausted";
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

    public bool IsOutcomeUncertain { get; private set; }
    [MaxLength(64)]
    public string? ErrorCode { get; private set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    [Required]
    [MaxLength(32)]
    public string Status { get; private set; } = StatusSucceeded;

    public int RetryCount { get; private set; }

    public DateTimeOffset? LastAttemptedAt { get; private set; }

    public DateTimeOffset? NextRetryAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? TargetItemId { get; private set; }

    public string? PayloadJson { get; private set; }
    public string? ResponseJson { get; private set; }

    public string? RefundCompletionJson { get; private set; }

    public string? ApiResponseJson { get; private set; }
    [MaxLength(64)]
    public string? PrerequisiteOperationId { get; private set; }

    [MaxLength(64)]
    public string? ErrorClassification { get; private set; }

    public bool IsTransient { get; private set; }

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
        DateTimeOffset createdAt,
        string? status = null,
        int retryCount = 0,
        DateTimeOffset? lastAttemptedAt = null,
        DateTimeOffset? nextRetryAt = null,
        DateTimeOffset? completedAt = null,
        Guid? targetItemId = null,
        string? payloadJson = null,
        string? responseJson = null,
        string? apiResponseJson = null,
        string? prerequisiteOperationId = null,
        string? errorClassification = null,
        bool isTransient = false,
        bool isOutcomeUncertain = false,
        string? refundCompletionJson = null)
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
        Status = status ?? (isSuccess ? StatusSucceeded : StatusFailed);
        RetryCount = retryCount;
        LastAttemptedAt = lastAttemptedAt;
        NextRetryAt = nextRetryAt;
        CompletedAt = completedAt ?? (isSuccess ? createdAt : null);
        TargetItemId = targetItemId;
        PayloadJson = payloadJson;
        ResponseJson = responseJson;
        ApiResponseJson = apiResponseJson;
        PrerequisiteOperationId = prerequisiteOperationId;
        ErrorClassification = errorClassification;
        IsTransient = isTransient;
        IsOutcomeUncertain = isOutcomeUncertain;
        RefundCompletionJson = refundCompletionJson;
    }

    internal static RentalOperationReceipt CreatePending(
        Guid id,
        Guid rentalOrderId,
        string operationId,
        string action,
        string fingerprint,
        DateTimeOffset createdAt,
        Guid? targetItemId = null,
        string? payloadJson = null)
    {
        return new RentalOperationReceipt(
            id,
            rentalOrderId,
            operationId,
            action,
            fingerprint,
            isSuccess: false,
            errorCode: null,
            errorMessage: null,
            createdAt: createdAt,
            status: StatusPending,
            retryCount: 0,
            lastAttemptedAt: null,
            nextRetryAt: null,
            completedAt: null,
            targetItemId: targetItemId,
            payloadJson: payloadJson,
            responseJson: null,
            isOutcomeUncertain: false,
            refundCompletionJson: null);
    }

    internal void MarkProcessing(DateTimeOffset attemptedAt)
    {
        Status = StatusProcessing;
        LastAttemptedAt = attemptedAt;
    }

    internal void MarkSucceeded(DateTimeOffset completedAt, string? responseJson = null, string? apiResponseJson = null)
    {
        IsSuccess = true;
        IsOutcomeUncertain = false;
        Status = StatusSucceeded;
        CompletedAt = completedAt;
        NextRetryAt = null;
        ErrorClassification = null;
        IsTransient = false;
        if (responseJson is not null)
        {
            ResponseJson = responseJson;
        }
        if (apiResponseJson is not null)
        {
            ApiResponseJson = apiResponseJson;
        }
    }

    internal void SetRefundCompletion(string refundCompletionJson, DateTimeOffset completedAt)
    {
        RefundCompletionJson = refundCompletionJson;
        CompletedAt = completedAt;
    }
    internal void SetApiResponse(string apiResponseJson)
    {
        ApiResponseJson = apiResponseJson;
    }

    internal void MarkFailed(string? errorCode, string? errorMessage, DateTimeOffset attemptedAt, string? classification = null, bool isTransient = false, DateTimeOffset? nextRetryAt = null)
    {
        IsSuccess = false;
        Status = StatusFailed;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        LastAttemptedAt = attemptedAt;
        NextRetryAt = nextRetryAt;
        ErrorClassification = classification;
        IsTransient = isTransient;
    }

    internal void MarkUncertain(string? errorCode, string? errorMessage, DateTimeOffset attemptedAt, DateTimeOffset? nextRetryAt = null)
    {
        IsSuccess = false;
        IsOutcomeUncertain = true;
        Status = StatusProcessing;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        LastAttemptedAt = attemptedAt;
        NextRetryAt = nextRetryAt;
        ErrorClassification = ClassificationUncertain;
        IsTransient = true;
    }

    internal void BeginProviderAttempt(DateTimeOffset attemptedAt)
    {
        RetryCount++;
        Status = StatusProcessing;
        LastAttemptedAt = attemptedAt;
    }

    internal void RecordAttemptFailure(
        string? errorCode,
        string? errorMessage,
        DateTimeOffset attemptedAt,
        DateTimeOffset? nextRetryAt,
        bool isUncertain,
        bool isExhausted)
    {
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        LastAttemptedAt = attemptedAt;
        NextRetryAt = nextRetryAt;

        if (isUncertain)
        {
            IsOutcomeUncertain = true;
        }

        if (isExhausted)
        {
            Status = StatusFailed;
            ErrorClassification = (isUncertain || IsOutcomeUncertain)
                ? ClassificationUncertainExhausted
                : ClassificationTransientExhausted;
            IsTransient = false;
        }
        else if (isUncertain || IsOutcomeUncertain)
        {
            Status = StatusProcessing;
            ErrorClassification = ClassificationUncertain;
            IsTransient = true;
        }
        else
        {
            Status = StatusPending;
            ErrorClassification = ClassificationTransient;
            IsTransient = true;
        }
    }

    internal void ReopenForManualReplay(DateTimeOffset requestedAt)
    {
        RetryCount = 0;
        Status = StatusPending;
        NextRetryAt = requestedAt;
        LastAttemptedAt = requestedAt;
        IsTransient = true;
    }

    internal void MarkManualReconciliationRequired(DateTimeOffset utcNow, string? reason = null)
    {
        Status = StatusManualReconciliationRequired;
        IsSuccess = false;
        IsTransient = false;
        NextRetryAt = null;
        LastAttemptedAt = utcNow;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            ErrorMessage = reason;
        }
    }

    internal void RecordRetryAttempt(DateTimeOffset attemptedAt, DateTimeOffset? nextRetryAt, string? errorMessage = null, string? classification = null)
    {
        RetryCount++;
        LastAttemptedAt = attemptedAt;
        NextRetryAt = nextRetryAt;
        IsTransient = true;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            ErrorMessage = errorMessage;
        }
        if (!string.IsNullOrWhiteSpace(classification))
        {
            ErrorClassification = classification;
        }
    }
}
