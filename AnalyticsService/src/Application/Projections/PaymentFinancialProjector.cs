using Dprental.AnalyticsService.Application.Errors;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Application.Events.Payment;
using Dprental.AnalyticsService.Common;
using Dprental.AnalyticsService.Common.Utils;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.IRepositories;

namespace Dprental.AnalyticsService.Application.Projections;

/// <summary>
/// Chiếu event payment.analytics.v1 vào payment_financial_events (cách lưu của Bảo, giữ nguyên).
/// Dữ liệu tài chính chỉ ghi một lần:
/// - cùng MessageId, hoặc cùng LogicalEventId + cùng số liệu → trùng, bỏ qua;
/// - cùng LogicalEventId nhưng khác số liệu → xung đột, báo lỗi (dead letter), không ghi đè.
/// Analytics không tự tính lại phí hay tiền phải trả NCC. Không gọi SaveChanges.
/// </summary>
public sealed class PaymentFinancialProjector
{
    private readonly IPaymentFinancialEventRepository _repository;
    private readonly ReadModelChangeSet _changes;

    public PaymentFinancialProjector(IPaymentFinancialEventRepository repository, ReadModelChangeSet changes)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    }

    /// <returns>true nếu ghi dòng mới; false nếu là bản trùng đã có.</returns>
    public Task<Result<bool>> ApplyAsync(KafkaEnvelope envelope, InvoicePaidFinancialPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.RentalAmount + payload.DepositAmount != payload.TotalAmount)
        {
            return Task.FromResult(Result.Failure<bool>(ProjectionErrors.FinancialFactsInvalid()));
        }

        PaymentFinancialEvent row = NewRow(envelope, $"{envelope.MessageType}:{payload.InvoiceId:D}");
        row.InvoiceId = payload.InvoiceId;
        row.InvoiceRef = payload.InvoiceRef;
        row.RentalOrderId = payload.RentalOrderId;
        row.RenterId = payload.RenterId;
        row.RentalAmount = payload.RentalAmount;
        row.DepositAmount = payload.DepositAmount;
        row.TotalAmount = payload.TotalAmount;
        row.PaidAt = payload.PaidAt.ToUniversalTime();
        return RecordAsync(row, payload.Currency, cancellationToken);
    }

    public Task<Result<bool>> ApplyAsync(KafkaEnvelope envelope, RentalItemFinancialSettledPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.FinalDeductionAmount > payload.DepositAmount ||
            payload.DepositRefundDue + payload.FinalDeductionAmount != payload.DepositAmount ||
            payload.PlatformFeeRate is < 0 or > 1 ||
            payload.PlatformFeeAmount > payload.RentalAmount ||
            payload.SupplierPayable != payload.RentalAmount - payload.PlatformFeeAmount + payload.FinalDeductionAmount)
        {
            return Task.FromResult(Result.Failure<bool>(ProjectionErrors.FinancialFactsInvalid()));
        }

        PaymentFinancialEvent row = NewRow(envelope, $"{envelope.MessageType}:{payload.SettlementRef}");
        row.SettlementId = payload.SettlementId;
        row.SettlementRef = payload.SettlementRef;
        row.RentalOrderId = payload.RentalOrderId;
        row.RentalItemId = payload.RentalItemId;
        row.SupplierId = payload.SupplierId;
        row.RentalAmount = payload.RentalAmount;
        row.DepositAmount = payload.DepositAmount;
        row.FinalDeductionAmount = payload.FinalDeductionAmount;
        row.DepositRefundDue = payload.DepositRefundDue;
        row.PlatformFeeRate = payload.PlatformFeeRate;
        row.PlatformFeeAmount = payload.PlatformFeeAmount;
        row.SupplierPayable = payload.SupplierPayable;
        row.CompletedAt = payload.CompletedAt.ToUniversalTime();
        // Payment hiện không gửi currency cho settlement; có gửi thì phải là VND
        return RecordAsync(row, payload.Currency ?? ProjectionErrors.SupportedCurrency, cancellationToken);
    }

    public Task<Result<bool>> ApplyAsync(KafkaEnvelope envelope, RefundCompletedFinancialPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        PaymentFinancialEvent row = NewRow(
            envelope, $"{envelope.MessageType}:{payload.RefundRef}:{payload.RentalItemId?.ToString("D") ?? "order"}");
        row.RefundId = payload.RefundId;
        row.RefundRef = payload.RefundRef;
        row.RentalOrderId = payload.RentalOrderId;
        row.RentalItemId = payload.RentalItemId;
        row.RenterId = payload.RenterId;
        row.RefundKind = payload.RefundKind;
        row.Amount = payload.Amount;
        row.CompletedAt = payload.CompletedAt.ToUniversalTime();
        return RecordAsync(row, payload.Currency, cancellationToken);
    }

    public Task<Result<bool>> ApplyAsync(KafkaEnvelope envelope, RefundRequestedFinancialPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        PaymentFinancialEvent row = NewRow(envelope, $"{envelope.MessageType}:{payload.RefundRef}");
        row.RefundRef = payload.RefundRef;
        row.RentalOrderId = payload.RentalOrderId;
        row.RentalItemId = payload.RentalItemId;
        row.RefundKind = payload.RefundKind;
        row.Amount = payload.Amount;
        row.RequestedAt = payload.RequestedAt.ToUniversalTime();
        return RecordAsync(row, payload.Currency, cancellationToken);
    }

    public Task<Result<bool>> ApplyAsync(KafkaEnvelope envelope, SupplierPayoutCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        PaymentFinancialEvent row = NewRow(envelope, $"{envelope.MessageType}:{payload.PayoutRef}");
        row.PayoutId = payload.PayoutId;
        row.PayoutRef = payload.PayoutRef;
        row.SettlementId = payload.SettlementId;
        row.RentalItemId = payload.RentalItemId;
        row.SupplierId = payload.SupplierId;
        row.Amount = payload.Amount;
        row.CompletedAt = payload.CompletedAt.ToUniversalTime();
        return RecordAsync(row, payload.Currency, cancellationToken);
    }

    private async Task<Result<bool>> RecordAsync(PaymentFinancialEvent row, string? currency, CancellationToken cancellationToken)
    {
        // 1. Kiểm tra dữ liệu
        if (!ProjectionErrors.IsSupportedCurrency(currency))
        {
            return ProjectionErrors.CurrencyInvalid(currency);
        }

        foreach (decimal? amount in Amounts(row))
        {
            if (amount is { } value && VndAmount.ToLong(value) is { IsFailure: true } invalid)
            {
                return invalid.Error;
            }
        }

        // 2. Chống trùng: giao lại cùng messageId, hoặc Payment phát lại cùng nghiệp vụ với messageId mới
        if (await _repository.ExistsMessageAsync(row.MessageId, cancellationToken))
        {
            return false;
        }

        PaymentFinancialEvent? existing = await _repository.FindByLogicalEventIdAsync(row.LogicalEventId, cancellationToken);
        if (existing is not null)
        {
            return SameFacts(existing, row)
                ? Result.Success(false)
                : Result.Failure<bool>(ProjectionErrors.FactConflict(row.LogicalEventId));
        }

        // 3. Ghi mới
        _repository.Add(row);
        _changes.TrackSupplier(row.SupplierId);
        return true;
    }

    private static PaymentFinancialEvent NewRow(KafkaEnvelope envelope, string logicalEventId)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return new PaymentFinancialEvent
        {
            MessageId = envelope.MessageId,
            MessageType = envelope.MessageType,
            LogicalEventId = logicalEventId,
            AggregateId = envelope.AggregateId ?? string.Empty,
            OccurredAt = envelope.OccurredAt.ToUniversalTime(),
            Currency = ProjectionErrors.SupportedCurrency
        };
    }

    private static decimal?[] Amounts(PaymentFinancialEvent row) =>
    [
        row.RentalAmount, row.DepositAmount, row.TotalAmount, row.FinalDeductionAmount,
        row.DepositRefundDue, row.PlatformFeeAmount, row.SupplierPayable, row.Amount
    ];

    // Cùng nghiệp vụ thì mọi số tiền và mốc thời gian phải giống hệt
    private static bool SameFacts(PaymentFinancialEvent a, PaymentFinancialEvent b) =>
        Amounts(a).SequenceEqual(Amounts(b))
        && a.PaidAt == b.PaidAt
        && a.CompletedAt == b.CompletedAt
        && a.RequestedAt == b.RequestedAt
        && a.RentalItemId == b.RentalItemId
        && a.RentalOrderId == b.RentalOrderId
        && a.RefundKind == b.RefundKind
        && a.SupplierId == b.SupplierId;
}
