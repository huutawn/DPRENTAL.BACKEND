using System.Text.Json;
using Dprental.AnalyticsService.Application.Events;
using Dprental.AnalyticsService.Domain.Entities;
using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AnalyticsService.Application.Events.Payment;

public enum PaymentAnalyticsIngestResult
{
    Recorded,
    Duplicate
}

/// <summary>
/// Validates and persists the safe financial projection of Payment analytics events.
/// Kafka transport/offset management is intentionally kept outside this application handler.
/// </summary>
public sealed class PaymentAnalyticsEventIngestor(AnalyticsServiceDbContext dbContext)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PaymentAnalyticsIngestResult> IngestAsync(
        JsonElement message,
        CancellationToken cancellationToken = default)
    {
        var envelope = message.Deserialize<AnalyticsEventEnvelope<JsonElement>>(JsonOptions)
            ?? throw new InvalidOperationException("Analytics event envelope is empty.");

        if (envelope.MessageId == Guid.Empty || envelope.Version != 1 || string.IsNullOrWhiteSpace(envelope.AggregateId))
        {
            throw new InvalidOperationException("Analytics event envelope is invalid.");
        }

        var record = envelope.MessageType switch
        {
            PaymentAnalyticsEventTypes.InvoicePaidFinancial =>
                Map(envelope, Deserialize<InvoicePaidFinancialPayload>(envelope.Payload)),
            PaymentAnalyticsEventTypes.RentalItemFinancialSettled =>
                Map(envelope, Deserialize<RentalItemFinancialSettledPayload>(envelope.Payload)),
            PaymentAnalyticsEventTypes.RefundCompletedFinancial =>
                Map(envelope, Deserialize<RefundCompletedFinancialPayload>(envelope.Payload)),
            PaymentAnalyticsEventTypes.SupplierPayoutCompleted =>
                Map(envelope, Deserialize<SupplierPayoutCompletedPayload>(envelope.Payload)),
            _ => throw new InvalidOperationException($"Unsupported Payment analytics event '{envelope.MessageType}'.")
        };

        ValidateMoney(record);

        if (await dbContext.PaymentFinancialEvents.AnyAsync(
                x => x.MessageId == record.MessageId || x.LogicalEventId == record.LogicalEventId,
                cancellationToken))
        {
            return PaymentAnalyticsIngestResult.Duplicate;
        }

        dbContext.PaymentFinancialEvents.Add(record);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return PaymentAnalyticsIngestResult.Recorded;
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery can pass the existence check; the unique indexes arbitrate it.
            dbContext.Entry(record).State = EntityState.Detached;
            if (await dbContext.PaymentFinancialEvents.AsNoTracking().AnyAsync(
                    x => x.MessageId == record.MessageId || x.LogicalEventId == record.LogicalEventId,
                    cancellationToken))
            {
                return PaymentAnalyticsIngestResult.Duplicate;
            }

            throw;
        }
    }

    private static T Deserialize<T>(JsonElement payload) =>
        payload.Deserialize<T>(JsonOptions)
        ?? throw new InvalidOperationException($"Event payload '{typeof(T).Name}' is empty.");

    private static PaymentFinancialEvent Map<T>(
        AnalyticsEventEnvelope<JsonElement> envelope,
        T payload) where T : notnull
    {
        var row = new PaymentFinancialEvent
        {
            MessageId = envelope.MessageId,
            MessageType = envelope.MessageType,
            AggregateId = envelope.AggregateId,
            OccurredAt = envelope.OccurredAt.ToUniversalTime(),
            Currency = "VND"
        };

        switch (payload)
        {
            case InvoicePaidFinancialPayload invoice:
                RequireVnd(invoice.Currency);
                row.LogicalEventId = $"{envelope.MessageType}:{invoice.InvoiceId:D}";
                row.InvoiceId = invoice.InvoiceId;
                row.InvoiceRef = invoice.InvoiceRef;
                row.RentalOrderId = invoice.RentalOrderId;
                row.RenterId = invoice.RenterId;
                row.RentalAmount = invoice.RentalAmount;
                row.DepositAmount = invoice.DepositAmount;
                row.TotalAmount = invoice.TotalAmount;
                row.PaidAt = invoice.PaidAt.ToUniversalTime();
                break;
            case RentalItemFinancialSettledPayload settlement:
                row.LogicalEventId = $"{envelope.MessageType}:{settlement.SettlementRef}";
                row.SettlementId = settlement.SettlementId;
                row.SettlementRef = settlement.SettlementRef;
                row.RentalOrderId = settlement.RentalOrderId;
                row.RentalItemId = settlement.RentalItemId;
                row.SupplierId = settlement.SupplierId;
                row.RentalAmount = settlement.RentalAmount;
                row.DepositAmount = settlement.DepositAmount;
                row.FinalDeductionAmount = settlement.FinalDeductionAmount;
                row.DepositRefundDue = settlement.DepositRefundDue;
                row.PlatformFeeRate = settlement.PlatformFeeRate;
                row.PlatformFeeAmount = settlement.PlatformFeeAmount;
                row.SupplierPayable = settlement.SupplierPayable;
                row.CompletedAt = settlement.CompletedAt.ToUniversalTime();
                break;
            case RefundCompletedFinancialPayload refund:
                RequireVnd(refund.Currency);
                row.LogicalEventId = $"{envelope.MessageType}:{refund.RefundRef}:{refund.RentalItemId?.ToString("D") ?? "order"}";
                row.RefundId = refund.RefundId;
                row.RefundRef = refund.RefundRef;
                row.RentalOrderId = refund.RentalOrderId;
                row.RentalItemId = refund.RentalItemId;
                row.RenterId = refund.RenterId;
                row.RefundKind = refund.RefundKind;
                row.Amount = refund.Amount;
                row.CompletedAt = refund.CompletedAt.ToUniversalTime();
                break;
            case SupplierPayoutCompletedPayload payout:
                RequireVnd(payout.Currency);
                row.LogicalEventId = $"{envelope.MessageType}:{payout.PayoutRef}";
                row.PayoutId = payout.PayoutId;
                row.PayoutRef = payout.PayoutRef;
                row.SettlementId = payout.SettlementId;
                row.RentalItemId = payout.RentalItemId;
                row.SupplierId = payout.SupplierId;
                row.Amount = payout.Amount;
                row.CompletedAt = payout.CompletedAt.ToUniversalTime();
                break;
            default:
                throw new InvalidOperationException("Unsupported Payment analytics payload.");
        }

        return row;
    }

    private static void RequireVnd(string currency)
    {
        if (!string.Equals(currency, "VND", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only VND financial events are supported in MVP.");
        }
    }

    private static void ValidateMoney(PaymentFinancialEvent row)
    {
        foreach (decimal? amount in new[]
        {
            row.RentalAmount, row.DepositAmount, row.TotalAmount, row.FinalDeductionAmount,
            row.DepositRefundDue, row.PlatformFeeAmount, row.SupplierPayable, row.Amount
        })
        {
            if (amount is < 0 || amount.HasValue && decimal.Truncate(amount.Value) != amount.Value)
            {
                throw new InvalidOperationException("Financial amount must be a non-negative whole number of VND.");
            }
        }
    }
}
