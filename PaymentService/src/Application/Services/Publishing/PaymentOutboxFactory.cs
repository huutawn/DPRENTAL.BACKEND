using System.Text.Json;
using Dprental.PaymentService.Application.DTOs.Events;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services;

internal static partial class PaymentOutboxFactory
{
    public static OutboxMessage CreateInvoicePaidOutboxMessage(
        Invoice invoice,
        DateTimeOffset occurredAt,
        DateTimeOffset? paidAt = null,
        string paymentDisposition = InvoicePaidDispositions.OnTime)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        Guid messageId = Guid.NewGuid();
        var eventPayload = new InvoicePaidPayload(
            invoice.RentalOrderId,
            invoice.RenterId,
            invoice.InvoiceRef,
            invoice.PaymentRef!,
            (decimal)decimal.ToInt64(invoice.TotalAmount),
            invoice.Currency,
            paidAt ?? invoice.PaidAt ?? occurredAt,
            paymentDisposition,
            occurredAt.ToUniversalTime());

        var envelope = new PaymentEventEnvelope<InvoicePaidPayload>(
            messageId,
            "InvoicePaid",
            1,
            "payment-service",
            invoice.RentalOrderId.ToString("D"),
            occurredAt,
            messageId.ToString("D"),
            null,
            eventPayload);

        return OutboxMessage.Create(
            envelope.MessageId,
            envelope.MessageType,
            "RentalOrder",
            envelope.AggregateId,
            JsonSerializer.Serialize(envelope),
            occurredAt);
    }
}
