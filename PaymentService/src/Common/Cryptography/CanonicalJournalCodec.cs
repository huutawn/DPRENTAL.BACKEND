using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.PaymentService.Common.Cryptography;

public sealed record CanonicalJournalDocument(
    int SchemaVersion,
    string Environment,
    Guid OperationId,
    Guid EventId,
    string StreamType,
    string StreamId,
    long Version,
    string EventType,
    decimal Amount,
    string Currency,
    string Status,
    string ConfirmationSource,
    string Actor,
    DateTimeOffset RecordedAtUtc,
    string PreviousHash,
    DateTimeOffset? BankTimeUtc = null,
    string? OutboxDigest = null,
    Guid? OutboxMessageId = null,
    IReadOnlyDictionary<string, string?>? RelatedEntities = null,
    IReadOnlyDictionary<string, object?>? Snapshot = null);

public static class CanonicalJournalCodec
{
    public const int CurrentSchemaVersion = 3;

    public static string NormalizeTimestamp(DateTimeOffset timestamp)
    {
        long ticks = timestamp.ToUniversalTime().Ticks;
        long microsecondTicks = (ticks / 10) * 10;
        var normalized = new DateTimeOffset(microsecondTicks, TimeSpan.Zero);
        return normalized.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }

    public static string FormatVndAmount(decimal amount)
    {
        if (decimal.Truncate(amount) != amount)
        {
            throw new InvalidOperationException($"Số tiền VND phải là số nguyên, nhận được: {amount}");
        }
        return decimal.ToInt64(amount).ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatFeeRate(decimal feeRate)
    {
        int scaled = (int)Math.Round(feeRate * 10000m, MidpointRounding.AwayFromZero);
        return scaled.ToString(CultureInfo.InvariantCulture);
    }

    public static decimal ParseFeeRate(string feeRateStr)
    {
        int scaled = int.Parse(feeRateStr, CultureInfo.InvariantCulture);
        return scaled / 10000m;
    }

    public static string SerializeToCanonicalJson(CanonicalJournalDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();

            writer.WriteString("actor", doc.Actor.Trim());
            writer.WriteString("amount", FormatVndAmount(doc.Amount));

            if (doc.BankTimeUtc.HasValue)
            {
                writer.WriteString("bankTimeUtc", NormalizeTimestamp(doc.BankTimeUtc.Value));
            }
            else
            {
                writer.WriteNull("bankTimeUtc");
            }

            writer.WriteString("confirmationSource", doc.ConfirmationSource.Trim().ToUpperInvariant());
            writer.WriteString("currency", doc.Currency.Trim().ToUpperInvariant());
            writer.WriteString("environment", doc.Environment.Trim().ToLowerInvariant());
            writer.WriteString("eventId", doc.EventId.ToString("D"));
            writer.WriteString("eventType", doc.EventType.Trim().ToUpperInvariant());
            writer.WriteString("operationId", doc.OperationId.ToString("D"));
            writer.WriteString("outboxDigest", doc.OutboxDigest?.Trim().ToLowerInvariant() ?? string.Empty);

            if (doc.OutboxMessageId.HasValue)
            {
                writer.WriteString("outboxMessageId", doc.OutboxMessageId.Value.ToString("D"));
            }
            else
            {
                writer.WriteNull("outboxMessageId");
            }

            writer.WriteString("previousHash", doc.PreviousHash.Trim().ToLowerInvariant());
            writer.WriteString("recordedAtUtc", NormalizeTimestamp(doc.RecordedAtUtc));

            writer.WriteStartObject("relatedEntities");
            if (doc.RelatedEntities is not null)
            {
                foreach (var kv in doc.RelatedEntities.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (kv.Value is null)
                    {
                        writer.WriteNull(kv.Key);
                    }
                    else
                    {
                        writer.WriteString(kv.Key, kv.Value.Trim());
                    }
                }
            }
            writer.WriteEndObject();

            writer.WriteNumber("schemaVersion", doc.SchemaVersion);

            writer.WriteStartObject("snapshot");
            if (doc.Snapshot is not null)
            {
                foreach (var kv in doc.Snapshot.OrderBy(k => k.Key, StringComparer.Ordinal))
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
                    else if (kv.Value is decimal decVal)
                    {
                        // Distinguish fee rates from monetary amounts
                        if (kv.Key.Contains("Rate", StringComparison.OrdinalIgnoreCase))
                        {
                            writer.WriteStringValue(FormatFeeRate(decVal));
                        }
                        else
                        {
                            writer.WriteStringValue(FormatVndAmount(decVal));
                        }
                    }
                    else if (kv.Value is long longVal)
                    {
                        writer.WriteNumberValue(longVal);
                    }
                    else if (kv.Value is int intVal)
                    {
                        writer.WriteNumberValue(intVal);
                    }
                    else if (kv.Value is bool boolVal)
                    {
                        writer.WriteBooleanValue(boolVal);
                    }
                    else if (kv.Value is Guid guidVal)
                    {
                        writer.WriteStringValue(guidVal.ToString("D"));
                    }
                    else
                    {
                        JsonSerializer.Serialize(writer, kv.Value);
                    }
                }
            }
            writer.WriteEndObject();

            writer.WriteString("status", doc.Status.Trim().ToUpperInvariant());
            writer.WriteString("streamId", doc.StreamId.Trim());
            writer.WriteString("streamType", doc.StreamType.Trim());
            writer.WriteNumber("version", doc.Version);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputePayloadHash(string canonicalJson)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(canonicalJson);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string ComputeEnvelopeDigest(string ciphertext, string nonce, string tag, string keyId)
    {
        string raw = $"{ciphertext}:{nonce}:{tag}:{keyId}";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static string ComputeEntryHash(
        string streamId,
        long version,
        Guid eventId,
        string previousHash,
        string payloadHash,
        string? envelopeDigest = null)
    {
        string raw = $"{streamId.Trim()}|{version}|{eventId:D}|{previousHash.Trim().ToLowerInvariant()}|{payloadHash.Trim().ToLowerInvariant()}|{envelopeDigest?.Trim().ToLowerInvariant() ?? string.Empty}";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static CanonicalJournalDocument DeserializeFromCanonicalJson(string canonicalJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);

        using var jsonDoc = JsonDocument.Parse(canonicalJson);
        var root = jsonDoc.RootElement;

        int schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        string environment = root.GetProperty("environment").GetString() ?? string.Empty;
        Guid operationId = Guid.Parse(root.GetProperty("operationId").GetString()!);
        Guid eventId = Guid.Parse(root.GetProperty("eventId").GetString()!);
        string streamType = root.GetProperty("streamType").GetString() ?? string.Empty;
        string streamId = root.GetProperty("streamId").GetString() ?? string.Empty;
        long version = root.GetProperty("version").GetInt64();
        string eventType = root.GetProperty("eventType").GetString() ?? string.Empty;
        decimal amount = decimal.Parse(root.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
        string currency = root.GetProperty("currency").GetString() ?? string.Empty;
        string status = root.GetProperty("status").GetString() ?? string.Empty;
        string confirmationSource = root.GetProperty("confirmationSource").GetString() ?? string.Empty;
        string actor = root.GetProperty("actor").GetString() ?? string.Empty;
        DateTimeOffset recordedAtUtc = DateTimeOffset.Parse(root.GetProperty("recordedAtUtc").GetString()!, CultureInfo.InvariantCulture);
        string previousHash = root.GetProperty("previousHash").GetString() ?? string.Empty;

        DateTimeOffset? bankTimeUtc = null;
        if (root.TryGetProperty("bankTimeUtc", out var btProp) && btProp.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(btProp.GetString()))
        {
            bankTimeUtc = DateTimeOffset.Parse(btProp.GetString()!, CultureInfo.InvariantCulture);
        }

        string? outboxDigest = null;
        if (root.TryGetProperty("outboxDigest", out var odProp) && odProp.ValueKind != JsonValueKind.Null)
        {
            outboxDigest = odProp.GetString();
        }

        Guid? outboxMessageId = null;
        if (root.TryGetProperty("outboxMessageId", out var omProp) && omProp.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(omProp.GetString()))
        {
            outboxMessageId = Guid.Parse(omProp.GetString()!);
        }

        Dictionary<string, string?>? relatedEntities = null;
        if (root.TryGetProperty("relatedEntities", out var reProp) && reProp.ValueKind == JsonValueKind.Object)
        {
            relatedEntities = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var prop in reProp.EnumerateObject())
            {
                relatedEntities[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
            }
        }

        Dictionary<string, object?>? snapshot = null;
        if (root.TryGetProperty("snapshot", out var snProp) && snProp.ValueKind == JsonValueKind.Object)
        {
            snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in snProp.EnumerateObject())
            {
                snapshot[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDecimal(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.GetRawText()
                };
            }
        }

        return new CanonicalJournalDocument(
            SchemaVersion: schemaVersion,
            Environment: environment,
            OperationId: operationId,
            EventId: eventId,
            StreamType: streamType,
            StreamId: streamId,
            Version: version,
            EventType: eventType,
            Amount: amount,
            Currency: currency,
            Status: status,
            ConfirmationSource: confirmationSource,
            Actor: actor,
            RecordedAtUtc: recordedAtUtc,
            PreviousHash: previousHash,
            BankTimeUtc: bankTimeUtc,
            OutboxDigest: outboxDigest,
            OutboxMessageId: outboxMessageId,
            RelatedEntities: relatedEntities,
            Snapshot: snapshot);
    }
}
