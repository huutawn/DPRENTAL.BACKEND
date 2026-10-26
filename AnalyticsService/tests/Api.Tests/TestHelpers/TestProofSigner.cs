using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dprental.AnalyticsService.Api.Tests.TestHelpers;

/// <summary>
/// Ký proof cho message payment.analytics.v1 theo đúng định dạng PaymentProofVerifier kiểm chứng,
/// bằng một key ECDSA P-256 sinh trong test. Cho phép làm hỏng từng phần để test nhánh lỗi.
/// </summary>
internal sealed class TestProofSigner : IDisposable
{
    public const string KeyId = "test-key-1";
    public const string Issuer = "dprental-integrity-host";
    public const string Environment = "production";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public string Sign(string topic, string? messageKey, string value, string keyId = KeyId, string audience = "analytics-service")
    {
        using JsonDocument message = JsonDocument.Parse(value);
        string signed = JsonSerializer.Serialize(new
        {
            schema = 1,
            keyId,
            issuer = Issuer,
            environment = Environment,
            audience,
            topic,
            messageKey,
            messageId = message.RootElement.GetProperty("messageId").GetGuid(),
            messageType = message.RootElement.GetProperty("messageType").GetString(),
            payloadDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))),
            eventId = Guid.NewGuid(),
            operationId = Guid.NewGuid(),
            issuedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        });

        byte[] signature = _key.SignData(
            Encoding.UTF8.GetBytes("DPRENTAL:PUBLICATION:V1:" + signed),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return JsonSerializer.Serialize(new { payload = signed, signature = Convert.ToBase64String(signature) });
    }

    public void Dispose() => _key.Dispose();
}
