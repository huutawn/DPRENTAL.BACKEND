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
        decimal? expectedAmount = null,
        string? expectedCurrency = null,
        string? expectedStatus = null,
        string? invoiceRef = null,
        Guid? expectedOrderId = null,
        Guid? expectedRenterId = null,
        Guid? rentalItemId = null,
        Guid? supplierId = null,
        string? requirementRef = null)
    {
        string raw = $"amount={expectedAmount?.ToString(CultureInfo.InvariantCulture)};currency={expectedCurrency?.Trim().ToUpperInvariant()};invoiceRef={invoiceRef?.Trim()};nonce={callerNonce.Trim()};orderId={expectedOrderId};paymentRef={paymentRef.Trim()};purpose={purpose.Trim().ToUpperInvariant()};renterId={expectedRenterId};rentalItemId={rentalItemId};requirementRef={requirementRef?.Trim()};status={expectedStatus?.Trim().ToUpperInvariant()};supplierId={supplierId}";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
