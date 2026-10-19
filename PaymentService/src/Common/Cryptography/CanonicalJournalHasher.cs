using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.PaymentService.Common.Cryptography;

public static class CanonicalJournalHasher
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputePayloadHash(
        decimal amount,
        string currency,
        string status,
        string confirmationSource,
        DateTimeOffset timestampUtc,
        string actor,
        IReadOnlyDictionary<string, string?>? relatedEntities,
        string? outboxDigest)
    {
        var canonicalString = BuildCanonicalPayloadString(
            amount,
            currency,
            status,
            confirmationSource,
            timestampUtc,
            actor,
            relatedEntities,
            outboxDigest);

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(canonicalString);
        return Convert.ToHexStringLower(SHA256.HashData(utf8Bytes));
    }

    public static string BuildCanonicalPayloadString(
        decimal amount,
        string currency,
        string status,
        string confirmationSource,
        DateTimeOffset timestampUtc,
        string actor,
        IReadOnlyDictionary<string, string?>? relatedEntities,
        string? outboxDigest)
    {
        var sb = new StringBuilder(256);
        sb.Append("actor=").Append(actor.Trim()).Append(';');
        sb.Append("amount=").Append(amount.ToString("0.################", CultureInfo.InvariantCulture)).Append(';');
        sb.Append("confirmationSource=").Append(confirmationSource.Trim().ToUpperInvariant()).Append(';');
        sb.Append("currency=").Append(currency.Trim().ToUpperInvariant()).Append(';');
        sb.Append("outboxDigest=").Append(outboxDigest?.Trim() ?? string.Empty).Append(';');

        sb.Append("relatedEntities=[");
        if (relatedEntities is not null && relatedEntities.Count > 0)
        {
            bool first = true;
            foreach (var kv in relatedEntities.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                sb.Append(kv.Key.Trim()).Append(':').Append(kv.Value?.Trim() ?? string.Empty);
                first = false;
            }
        }
        sb.Append("];");

        sb.Append("status=").Append(status.Trim().ToUpperInvariant()).Append(';');
        sb.Append("timestampUtc=").Append(timestampUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));

        return sb.ToString();
    }

    public static string ComputeEntryHash(
        string streamId,
        long version,
        Guid eventId,
        string previousHash,
        string payloadHash)
    {
        string raw = $"{streamId.Trim()}|{version}|{eventId:D}|{previousHash.Trim().ToLowerInvariant()}|{payloadHash.Trim().ToLowerInvariant()}";
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexStringLower(SHA256.HashData(utf8Bytes));
    }

    public static string ComputeOutboxDigest(string? outboxPayload)
    {
        if (string.IsNullOrWhiteSpace(outboxPayload))
        {
            return string.Empty;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(outboxPayload);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string ComputeFingerprint(string streamId, Guid eventId, string payloadHash)
    {
        string prefix = payloadHash.Length >= 16 ? payloadHash[..16] : payloadHash;
        return $"DPRENTAL:{streamId}:{eventId:N}:{prefix}";
    }

    public static string ComputeRequestHash(
        string paymentRef,
        string purpose,
        string callerNonce,
        string? callerIdentity = null,
        decimal? expectedAmount = null,
        string? expectedCurrency = null,
        string? invoiceRef = null,
        Guid? expectedOrderId = null,
        Guid? expectedRenterId = null,
        Guid? rentalItemId = null,
        Guid? supplierId = null,
        string? requirementRef = null,
        string? expectedOutboxDigest = null,
        string? topic = null,
        string? messageKey = null,
        string? expectedStatus = null)
    {
        var sb = new StringBuilder(256);
        sb.Append("amount=").Append(expectedAmount.HasValue ? Dprental.PaymentService.Common.Utils.VndAmountValidator.FormatVndAmount(expectedAmount.Value) : string.Empty).Append(';');
        sb.Append("callerIdentity=").Append(callerIdentity?.Trim() ?? string.Empty).Append(';');
        sb.Append("currency=").Append(expectedCurrency?.Trim().ToUpperInvariant() ?? string.Empty).Append(';');
        sb.Append("expectedOutboxDigest=").Append(expectedOutboxDigest?.Trim().ToLowerInvariant() ?? string.Empty).Append(';');
        sb.Append("invoiceRef=").Append(invoiceRef?.Trim() ?? string.Empty).Append(';');
        sb.Append("messageKey=").Append(messageKey?.Trim() ?? string.Empty).Append(';');
        sb.Append("nonce=").Append(callerNonce.Trim()).Append(';');
        sb.Append("orderId=").Append(expectedOrderId.HasValue ? expectedOrderId.Value.ToString("D") : string.Empty).Append(';');
        sb.Append("paymentRef=").Append(paymentRef.Trim()).Append(';');
        sb.Append("purpose=").Append(purpose.Trim().ToUpperInvariant()).Append(';');
        sb.Append("renterId=").Append(expectedRenterId.HasValue ? expectedRenterId.Value.ToString("D") : string.Empty).Append(';');
        sb.Append("rentalItemId=").Append(rentalItemId.HasValue ? rentalItemId.Value.ToString("D") : string.Empty).Append(';');
        sb.Append("requirementRef=").Append(requirementRef?.Trim() ?? string.Empty).Append(';');
        sb.Append("supplierId=").Append(supplierId.HasValue ? supplierId.Value.ToString("D") : string.Empty).Append(';');
        sb.Append("topic=").Append(topic?.Trim() ?? string.Empty);

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
