using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Domain.Entities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.PaymentService.Application.Services;

internal static partial class PaymentOutboxFactory
{
    public static OutboxMessage CreateInvoicePaidFinancialOutboxMessage(
        Invoice invoice,
        DateTimeOffset occurredAt,
        DateTimeOffset? paidAt = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (invoice.Status != InvoiceStatus.Paid)
        {
            throw new InvalidOperationException("InvoicePaidFinancial chỉ được tạo cho invoice đã PAID.");
        }

        DateTimeOffset effectivePaidAt = (paidAt ?? invoice.PaidAt)
            ?? throw new InvalidOperationException("Invoice PAID thiếu PaidAt.");
        DateTimeOffset occurredAtUtc = occurredAt.ToUniversalTime();

        Guid messageId = CreateStableMessageId(OutboxTopics.InvoicePaidFinancialMessageType, invoice.Id);

        var payload = new InvoicePaidFinancialPayload(
            invoice.Id,
            invoice.InvoiceRef,
            invoice.RentalOrderId,
            invoice.RenterId,
            invoice.TotalRentalAmount,
            invoice.TotalDepositAmount,
            invoice.TotalAmount,
            invoice.Currency,
            effectivePaidAt.ToUniversalTime());

        var envelope = new PaymentEventEnvelope<InvoicePaidFinancialPayload>(
            messageId,
            OutboxTopics.InvoicePaidFinancialMessageType,
            1,
            "payment-service",
            invoice.RentalOrderId.ToString("D"),
            occurredAtUtc,
            messageId.ToString("D"),
            null,
            payload);

        return OutboxMessage.Create(
            envelope.MessageId,
            envelope.MessageType,
            "RentalOrder",
            envelope.AggregateId,
            JsonSerializer.Serialize(envelope),
            occurredAtUtc,
            topic: OutboxTopics.PaymentAnalyticsV1);
    }

    public static OutboxMessage CreateRentalItemFinancialSettled(
        RentalItemSettlementRecord settlement, Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(invoice);
        if (settlement.Status != SettlementState.Completed || settlement.CompletedAt is null)
            throw new InvalidOperationException("Financial settlement event requires completed financial obligations.");

        var payload = new RentalItemFinancialSettledPayload(settlement.Id, settlement.SettlementRef,
            invoice.RentalOrderId, settlement.RentalItemId, settlement.SupplierId,
            settlement.RentalAmount, settlement.DepositAmount, settlement.FinalDeductionAmount,
            settlement.DepositRefundDue, settlement.PlatformFeeRate, settlement.PlatformFeeAmount,
            settlement.SupplierPayable, settlement.CompletedAt.Value);
        return CreateFinancialOutbox(OutboxTopics.RentalItemFinancialSettledMessageType,
            settlement.Id, invoice.RentalOrderId.ToString("D"), settlement.CompletedAt.Value, payload);
    }

    public static OutboxMessage CreateRefundCompletedFinancial(
        ManualRefundRequirement refund, Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(refund);
        ArgumentNullException.ThrowIfNull(invoice);
        string? refundKind = refund.FinancialRefundKind ??
            (refund.Kind == ManualRefundKind.SettlementDeposit ? FinancialRefundKinds.SettlementDepositRefund : null);
        if (refund.Status != ManualRefundStatus.Completed || refund.CompletedAt is null ||
            string.IsNullOrWhiteSpace(refundKind))
            throw new InvalidOperationException("Financial refund event requires a completed, classified refund.");

        var payload = new RefundCompletedFinancialPayload(refund.Id, refund.RequirementRef,
            invoice.RentalOrderId, refund.RentalItemId, refund.RenterId, refundKind,
            refund.Amount, invoice.Currency, refund.CompletedAt.Value);
        return CreateFinancialOutbox(OutboxTopics.RefundCompletedFinancialMessageType,
            refund.Id, invoice.RentalOrderId.ToString("D"), refund.CompletedAt.Value, payload);
    }

    public static OutboxMessage CreateSupplierPayoutCompleted(SupplierPayout payout)
    {
        ArgumentNullException.ThrowIfNull(payout);
        if (payout.Status != SupplierPayout.StatusCompleted || payout.CompletedAt is null)
            throw new InvalidOperationException("Financial payout event requires a completed payout.");

        var payload = new SupplierPayoutCompletedPayload(payout.Id, payout.PayoutRef,
            payout.SettlementId, payout.RentalItemId, payout.SupplierId,
            payout.Amount, payout.Currency, payout.CompletedAt.Value);
        return CreateFinancialOutbox(OutboxTopics.SupplierPayoutCompletedMessageType,
            payout.Id, payout.Id.ToString("D"), payout.CompletedAt.Value, payload,
            aggregateType: "SupplierPayout");
    }

    private static OutboxMessage CreateFinancialOutbox<T>(
        string messageType, Guid sourceId, string aggregateId, DateTimeOffset occurredAt, T payload,
        string aggregateType = "RentalOrder")
    {
        Guid messageId = CreateStableMessageId(messageType, sourceId);
        var envelope = new PaymentEventEnvelope<T>(messageId, messageType, 1, "payment-service",
            aggregateId, occurredAt.ToUniversalTime(), messageId.ToString("D"), null, payload);
        return OutboxMessage.Create(messageId, messageType, aggregateType, aggregateId,
            JsonSerializer.Serialize(envelope), occurredAt.ToUniversalTime(),
            topic: OutboxTopics.PaymentAnalyticsV1);
    }

    /// <summary>GUID xác định theo (messageType, invoiceId), cùng cặp luôn ra cùng messageId.</summary>
    private static Guid CreateStableMessageId(string messageType, Guid invoiceId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"dprental.payment:{messageType}:{invoiceId:D}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
