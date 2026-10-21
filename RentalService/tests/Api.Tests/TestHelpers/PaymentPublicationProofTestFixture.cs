using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.RentalService.Application.Kafka.Configuration;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

internal static class PaymentPublicationProofTestFixture
{
    private const string KeyId = "rental-tests-p256";
    private static readonly ECDsa SigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Convert.ToBase64String(SigningKey.ExportSubjectPublicKeyInfo());
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    public static KafkaOptions Configure(KafkaOptions options)
    {
        options.PaymentProofIssuer = "test-integrity-host";
        options.PaymentProofEnvironment = "test";
        options.PaymentProofPublicKeys = new Dictionary<string, string>(StringComparer.Ordinal) { [KeyId] = PublicKey };
        return options;
    }

    public static string Create(string envelopeJson, string topic = "payment.events.v1", string? messageKey = null)
    {
        using var envelope = JsonDocument.Parse(envelopeJson);
        var root = envelope.RootElement;
        string signedBody = JsonSerializer.Serialize(new
        {
            schema = 1,
            issuer = "test-integrity-host",
            audience = "rental-service",
            environment = "test",
            keyId = KeyId,
            messageId = root.GetProperty("messageId").GetGuid(),
            eventId = Guid.NewGuid(),
            operationId = Guid.NewGuid(),
            topic,
            messageKey = messageKey ?? root.GetProperty("aggregateId").GetString(),
            messageType = root.GetProperty("messageType").GetString(),
            payloadDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(envelopeJson))),
            issuedAt = DateTimeOffset.UtcNow
        }, JsonOptions);
        byte[] signature = SigningKey.SignData(Encoding.UTF8.GetBytes("DPRENTAL:PUBLICATION:V1:" + signedBody),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return JsonSerializer.Serialize(new { payload = signedBody, signature = Convert.ToBase64String(signature) }, JsonOptions);
    }
}
