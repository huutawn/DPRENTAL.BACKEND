using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.PaymentService.Domain.Entities;

namespace Dprental.PaymentService.Application.Services.Integrity;

public static class OperationalSnapshotEngine
{
    public static IReadOnlyDictionary<string, object?> CreateInvoiceSnapshot(Invoice invoice, long revision, int? snapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        int version = snapshotVersion ?? invoice.IntegritySnapshotVersion;
        if (version <= 1)
        {
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
            ["paymentCode"] = invoice.PaymentCode.Trim(),
            ["cancellationOrigin"] = invoice.CancellationOrigin ?? string.Empty
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
            ["invoiceId"] = (item.InvoiceId != Guid.Empty ? item.InvoiceId : (item.Invoice?.Id ?? Guid.Empty)).ToString("D"),
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
            ["settlementId"] = refund.SettlementId?.ToString("D") ?? string.Empty,
            ["rentalItemId"] = refund.RentalItemId?.ToString("D") ?? string.Empty,
            ["amount"] = CanonicalJournalCodec.FormatVndAmount(refund.Amount),
            ["kind"] = refund.Kind.ToString(),
            ["operationKey"] = refund.OperationKey,
            ["items"] = JsonSerializer.Serialize(refund.Items.OrderBy(x => x.RentalItemId).Select(x => new
            {
                x.RentalItemId, RentalAmount = CanonicalJournalCodec.FormatVndAmount(x.RentalAmount),
                DepositAmount = CanonicalJournalCodec.FormatVndAmount(x.DepositAmount)
            })),
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
            ["resultHash"] = Dprental.PaymentService.Common.Cryptography.CanonicalJournalHasher.ComputeOutboxDigest(receipt.ResultJson)
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
            ["payloadHash"] = Dprental.PaymentService.Common.Cryptography.CanonicalJournalHasher.ComputeOutboxDigest(outbox.Payload)
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
                    writer.WriteStringValue(strVal.Trim());
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
                    if (decimal.Truncate(decVal) == decVal)
                    {
                        writer.WriteStringValue(CanonicalJournalCodec.FormatVndAmount(decVal));
                    }
                    else
                    {
                        writer.WriteStringValue(CanonicalJournalCodec.FormatFeeRate(decVal));
                    }
                }
                else if (kv.Value is bool boolVal)
                {
                    writer.WriteBooleanValue(boolVal);
                }
                else if (kv.Value is Guid guidVal)
                {
                    writer.WriteStringValue(guidVal.ToString("D"));
                }
                else if (kv.Value is DateTimeOffset dtoVal)
                {
                    writer.WriteStringValue(CanonicalJournalCodec.NormalizeTimestamp(dtoVal));
                }
                else
                {
                    throw new InvalidOperationException($"Snapshot không hỗ trợ kiểu dữ liệu '{kv.Value.GetType().Name}' cho trường '{kv.Key}'.");
                }
            }
            writer.WriteEndObject();
        }

        byte[] bytes = stream.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static CanonicalEntityClaim CreateInvoiceClaim(Invoice invoice, int? snapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var snap = CreateInvoiceSnapshot(invoice, invoice.Revision, snapshotVersion);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("Invoice", invoice.InvoiceRef, invoice.Revision, digest);
    }

    public static CanonicalEntityClaim CreateInvoiceItemClaim(InvoiceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var snap = CreateInvoiceItemSnapshot(item, item.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("InvoiceItem", item.RentalItemId.ToString("D"), item.Revision, digest);
    }

    public static CanonicalEntityClaim CreatePaymentTransactionClaim(PaymentTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(tx);
        var snap = CreatePaymentTransactionSnapshot(tx, tx.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("PaymentTransaction", tx.PaymentRef, tx.Revision, digest);
    }

    public static CanonicalEntityClaim CreateSettlementClaim(RentalItemSettlementRecord settlement)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        var snap = CreateSettlementRecordSnapshot(settlement, settlement.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("RentalItemSettlement", settlement.SettlementRef, settlement.Revision, digest);
    }

    public static CanonicalEntityClaim CreateLedgerEntryClaim(SettlementLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var snap = CreateLedgerEntrySnapshot(entry, entry.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("SettlementLedgerEntry", entry.Id.ToString("D"), entry.Revision, digest);
    }

    public static CanonicalEntityClaim CreateManualRefundClaim(ManualRefundRequirement refund)
    {
        ArgumentNullException.ThrowIfNull(refund);
        var snap = CreateManualRefundSnapshot(refund, refund.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("ManualRefundRequirement", refund.RequirementRef, refund.Revision, digest);
    }

    public static CanonicalEntityClaim CreateOperationReceiptClaim(OperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var snap = CreateOperationReceiptSnapshot(receipt, receipt.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("OperationReceipt", receipt.OperationKey, receipt.Revision, digest);
    }

    public static CanonicalEntityClaim CreateOutboxClaim(OutboxMessage outbox)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        var snap = CreateOutboxSnapshot(outbox, outbox.Revision);
        string digest = ComputeSnapshotDigest(snap);
        return new CanonicalEntityClaim("OutboxMessage", outbox.MessageId.ToString("D"), outbox.Revision, digest);
    }
}
