using System.Text.Json;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services;

internal static partial class PaymentOutboxFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static OutboxMessage CreateRentalSettled(
        RentalItemSettlementRecord settlement, Invoice invoice,
        string? refundRef, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(invoice);

        if (settlement.Status != SettlementState.Completed ||
            string.IsNullOrWhiteSpace(invoice.PaymentRef))
            throw new InvalidOperationException("RentalSettled requires a completed settlement and paid invoice.");

        var messageId = Guid.NewGuid();
        var envelope = new
        {
            MessageId = messageId,
            MessageType = "RentalSettled",
            Version = 1,
            Producer = "payment-service",
            AggregateId = invoice.RentalOrderId.ToString("D"),
            OccurredAt = completedAt,
            CorrelationId = messageId.ToString("D"),
            CausationId = (string?)null,
            Payload = new
            {
                invoice.RentalOrderId,
                settlement.RentalItemId,
                invoice.InvoiceRef,
                invoice.PaymentRef,
                settlement.SettlementRef,
                settlement.OperationKey,
                settlement.FinalDeductionAmount,
                RefundedDepositAmount = settlement.DepositRefundDue,
                invoice.Currency,
                RefundRef = refundRef,
                CompletedAt = completedAt
            }
        };

        return OutboxMessage.Create(
            messageId, "RentalSettled", "RentalOrder", envelope.AggregateId,
            JsonSerializer.Serialize(envelope, JsonOptions), completedAt);
    }
    internal static OutboxMessage CreateRefundCompleted(ManualRefundRequirement refund, Invoice invoice, DateTimeOffset now)
    {
        Guid id = Guid.NewGuid();
        var payload = new
        {
            invoice.RentalOrderId, invoice.InvoiceRef, invoice.PaymentRef,
            RefundRef = refund.RequirementRef, refund.OperationKey,
            TotalRefundedAmount = refund.Amount, invoice.Currency,
            Items = refund.Items.Select(x => new { x.RentalItemId, RefundedRentalAmount = x.RentalAmount, RefundedDepositAmount = x.DepositAmount }),
            CompletedAt = now
        };
        var envelope = new { MessageId = id, MessageType = "RefundCompleted", Version = 1,
            Producer = "payment-service", AggregateId = invoice.RentalOrderId.ToString("D"), OccurredAt = now,
            CorrelationId = refund.OperationKey, CausationId = (Guid?)null, Payload = payload };
        return OutboxMessage.Create(id, "RefundCompleted", "RentalOrder", envelope.AggregateId,
            JsonSerializer.Serialize(envelope, JsonOptions), now);
    }

}
