using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.AnalyticsService.Api.Background;

public static class PaymentAnalyticsProofVerifier
{
    public static void Verify(string payload, string? proofJson, string topic, string key,
        AnalyticsKafkaOptions options)
    {
        if (string.IsNullOrWhiteSpace(proofJson) || proofJson.Length > 16_384)
            throw new InvalidOperationException("Missing Payment publication proof.");

        using var proof = JsonDocument.Parse(proofJson);
        string signed = proof.RootElement.GetProperty("payload").GetString()!;
        string signature = proof.RootElement.GetProperty("signature").GetString()!;
        using var body = JsonDocument.Parse(signed);
        var b = body.RootElement;
        string kid = b.GetProperty("keyId").GetString()!;
        if (!options.ProofPublicKeys.TryGetValue(kid, out string? publicKey))
            throw new InvalidOperationException("Payment publication proof key is unavailable.");

        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (verifier.KeySize != 256 || !verifier.VerifyData(
            Encoding.UTF8.GetBytes("DPRENTAL:PUBLICATION:V1:" + signed),
            Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidOperationException("Invalid Payment publication signature.");

        using var envelope = JsonDocument.Parse(payload);
        var e = envelope.RootElement;
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        if (b.GetProperty("schema").GetInt32() != 1 ||
            b.GetProperty("issuer").GetString() != options.ProofIssuer ||
            b.GetProperty("environment").GetString() != options.ProofEnvironment ||
            b.GetProperty("audience").GetString() != "analytics-service" ||
            b.GetProperty("topic").GetString() != topic ||
            b.GetProperty("messageKey").GetString() != key ||
            b.GetProperty("messageId").GetGuid() != e.GetProperty("messageId").GetGuid() ||
            b.GetProperty("messageType").GetString() != e.GetProperty("messageType").GetString() ||
            b.GetProperty("payloadDigest").GetString() != digest ||
            b.GetProperty("eventId").GetGuid() == Guid.Empty ||
            b.GetProperty("operationId").GetGuid() == Guid.Empty ||
            b.GetProperty("issuedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddSeconds(1))
            throw new InvalidOperationException("Payment publication proof does not bind to this message.");
    }
}
