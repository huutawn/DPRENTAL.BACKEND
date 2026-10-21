using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Common;
namespace Dprental.RentalService.Application.Kafka.Consumers;

/// <summary>Verify durable event evidence without rejecting legitimate Kafka backlog on receipt TTL.</summary>
public static class PaymentPublicationProofVerifier
{
    public static Result Verify(string payload, string? proofJson, string? topic, string? key, KafkaOptions options, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(proofJson) || proofJson.Length > 16_384)
            return Error.Validation("PAYMENT_PROOF_MISSING", "Payment event requires publication proof.");
        try
        {
            using var proof = JsonDocument.Parse(proofJson);
            string signedPayload = proof.RootElement.GetProperty("payload").GetString()!;
            string signature = proof.RootElement.GetProperty("signature").GetString()!;
            using var body = JsonDocument.Parse(signedPayload);
            var b = body.RootElement;
            string keyId = b.GetProperty("keyId").GetString()!;
            if (!options.PaymentProofPublicKeys.TryGetValue(keyId, out var publicKey))
                return Error.ServiceUnavailable("PAYMENT_PROOF_KEY_UNAVAILABLE", "Payment publication key is unavailable; retry after key rotation is synchronized.");
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (verifier.KeySize != 256 || !verifier.VerifyData(Encoding.UTF8.GetBytes("DPRENTAL:PUBLICATION:V1:" + signedPayload),
                Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Error.Validation("PAYMENT_PROOF_INVALID", "Invalid payment publication signature.");
            using var envelope = JsonDocument.Parse(payload);
            var e = envelope.RootElement;
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            if (b.GetProperty("schema").GetInt32() != 1 || b.GetProperty("issuer").GetString() != options.PaymentProofIssuer ||
                b.GetProperty("environment").GetString() != options.PaymentProofEnvironment || b.GetProperty("audience").GetString() != "rental-service" ||
                b.GetProperty("topic").GetString() != (topic ?? options.PaymentEventsTopic) ||
                b.GetProperty("messageKey").GetString() != (key ?? e.GetProperty("aggregateId").GetString()) ||
                b.GetProperty("messageId").GetGuid() != e.GetProperty("messageId").GetGuid() ||
                b.GetProperty("messageType").GetString() != e.GetProperty("messageType").GetString() ||
                b.GetProperty("payloadDigest").GetString() != digest ||
                b.GetProperty("eventId").GetGuid() == Guid.Empty || b.GetProperty("operationId").GetGuid() == Guid.Empty ||
                b.GetProperty("issuedAt").GetDateTimeOffset() > now.AddSeconds(1))
                return Error.Validation("PAYMENT_PROOF_BINDING_MISMATCH", "Publication proof does not match event payload, routing or environment.");
            return Result.Success();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return Error.Validation("PAYMENT_PROOF_MALFORMED", "Malformed payment publication proof.");
        }
    }
}
