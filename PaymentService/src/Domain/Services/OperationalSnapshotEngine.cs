using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Common.Cryptography;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Domain.Services;

public static class OperationalSnapshotEngine
{
    public static IReadOnlyDictionary<string, object?> CreateInvoiceSnapshot(Invoice invoice, long revision)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "Invoice",
            ["entityId"] = invoice.InvoiceRef,
            ["revision"] = revision,
            ["orderId"] = invoice.RentalOrderId.ToString("D"),
            ["renterId"] = invoice.RenterId.ToString("D"),
            ["totalAmount"] = CanonicalJournalCodec.FormatVndAmount(invoice.TotalAmount),
            ["depositAmount"] = CanonicalJournalCodec.FormatVndAmount(invoice.TotalDepositAmount),
            ["rentalAmount"] = CanonicalJournalCodec.FormatVndAmount(invoice.TotalRentalAmount),
            ["currency"] = invoice.Currency.Trim().ToUpperInvariant(),
            ["status"] = invoice.Status.ToString(),
            ["paymentDeadline"] = CanonicalJournalCodec.NormalizeTimestamp(invoice.PaymentDeadline),
            ["paymentCode"] = invoice.PaymentCode.Trim()
        };
    }

    public static IReadOnlyDictionary<string, object?> CreateInvoiceItemSnapshot(InvoiceItem item, long revision)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "InvoiceItem",
            ["entityId"] = item.RentalItemId.ToString("D"),
            ["revision"] = revision,
            ["invoiceId"] = item.InvoiceId.ToString("D"),
            ["supplierId"] = item.SupplierId.ToString("D"),
            ["rentalAmount"] = CanonicalJournalCodec.FormatVndAmount(item.RentalAmount),
            ["depositAmount"] = CanonicalJournalCodec.FormatVndAmount(item.DepositAmount),
            ["totalAmount"] = CanonicalJournalCodec.FormatVndAmount(item.TotalAmount)
        };
    }

    public static IReadOnlyDictionary<string, object?> CreatePaymentTransactionSnapshot(PaymentTransaction tx, long revision)
    {
        ArgumentNullException.ThrowIfNull(tx);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "PaymentTransaction",
            ["entityId"] = tx.PaymentRef,
            ["revision"] = revision,
            ["provider"] = tx.Provider.Trim(),
            ["providerTransactionId"] = tx.ProviderTransactionId.Trim(),
            ["gateway"] = tx.Gateway.Trim(),
            ["transferAmount"] = CanonicalJournalCodec.FormatVndAmount(tx.TransferAmount),
            ["transferType"] = tx.TransferType.Trim().ToUpperInvariant(),
            ["transactionDate"] = CanonicalJournalCodec.NormalizeTimestamp(tx.TransactionDate),
            ["status"] = tx.Status.Trim().ToUpperInvariant()
        };
    }

    public static IReadOnlyDictionary<string, object?> CreateSettlementRecordSnapshot(RentalItemSettlementRecord settlement, long revision)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "RentalItemSettlement",
            ["entityId"] = settlement.SettlementRef,
            ["revision"] = revision,
            ["invoiceId"] = settlement.InvoiceId.ToString("D"),
            ["rentalItemId"] = settlement.RentalItemId.ToString("D"),
            ["platformFeeRate"] = CanonicalJournalCodec.FormatFeeRate(settlement.PlatformFeeRate),
            ["platformFeeAmount"] = CanonicalJournalCodec.FormatVndAmount(settlement.PlatformFeeAmount),
            ["finalDeductionAmount"] = CanonicalJournalCodec.FormatVndAmount(settlement.FinalDeductionAmount),
            ["depositRefundDue"] = CanonicalJournalCodec.FormatVndAmount(settlement.DepositRefundDue),
            ["supplierPayable"] = CanonicalJournalCodec.FormatVndAmount(settlement.SupplierPayable),
            ["status"] = settlement.Status.ToString()
        };
    }

    public static IReadOnlyDictionary<string, object?> CreateLedgerEntrySnapshot(SettlementLedgerEntry entry, long revision)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "SettlementLedgerEntry",
            ["entityId"] = entry.Id.ToString("D"),
            ["revision"] = revision,
            ["settlementId"] = entry.SettlementId.ToString("D"),
            ["entryType"] = entry.EntryType.ToString(),
            ["amount"] = CanonicalJournalCodec.FormatVndAmount(entry.Amount)
        };
    }

    public static IReadOnlyDictionary<string, object?> CreateManualRefundSnapshot(ManualRefundRequirement refund, long revision)
    {
        ArgumentNullException.ThrowIfNull(refund);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "ManualRefundRequirement",
            ["entityId"] = refund.RequirementRef,
            ["revision"] = revision,
            ["settlementId"] = refund.SettlementId.ToString("D"),
            ["rentalItemId"] = refund.RentalItemId.ToString("D"),
            ["amount"] = CanonicalJournalCodec.FormatVndAmount(refund.Amount),
            ["status"] = refund.Status.ToString(),
            ["recipientAdminId"] = refund.CompletedBy?.ToString("D") ?? string.Empty,
            ["externalTransferReference"] = refund.ExternalTransferReference?.Trim() ?? string.Empty
        };
    }

    public static IReadOnlyDictionary<string, object?> CreateOperationReceiptSnapshot(OperationReceipt receipt, long revision)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "OperationReceipt",
            ["entityId"] = receipt.OperationKey,
            ["revision"] = revision,
            ["operationType"] = receipt.OperationType.Trim(),
            ["payloadFingerprint"] = receipt.PayloadFingerprint.Trim(),
            ["resultHash"] = CanonicalJournalHasher.ComputeOutboxDigest(receipt.ResultJson)
        };
    }

    public static IReadOnlyDictionary<string, object?> CreateOutboxSnapshot(OutboxMessage outbox, long revision)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entityType"] = "OutboxMessage",
            ["entityId"] = outbox.MessageId.ToString("D"),
            ["revision"] = revision,
            ["aggregateType"] = outbox.AggregateType.Trim(),
            ["aggregateId"] = outbox.AggregateId.Trim(),
            ["messageType"] = outbox.MessageType.Trim(),
            ["payloadHash"] = CanonicalJournalHasher.ComputeOutboxDigest(outbox.Payload)
        };
    }

    public static string ComputeSnapshotDigest(IReadOnlyDictionary<string, object?> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var kv in snapshot.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(kv.Key);
                if (kv.Value is null)
                {
                    writer.WriteNullValue();
                }
                else if (kv.Value is string strVal)
                {
                    writer.WriteStringValue(strVal);
                }
                else if (kv.Value is long longVal)
                {
                    writer.WriteNumberValue(longVal);
                }
                else if (kv.Value is int intVal)
                {
                    writer.WriteNumberValue(intVal);
                }
                else if (kv.Value is decimal decVal)
                {
                    writer.WriteNumberValue(decVal);
                }
                else if (kv.Value is bool boolVal)
                {
                    writer.WriteBooleanValue(boolVal);
                }
                else
                {
                    JsonSerializer.Serialize(writer, kv.Value);
                }
            }
            writer.WriteEndObject();
        }

        byte[] bytes = stream.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
