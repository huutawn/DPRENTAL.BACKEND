using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dprental.AnalyticsService.Application.Kafka.Configuration;
using Dprental.AnalyticsService.Common;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Kafka.Consumers;

/// <summary>
/// Kiểm chứng proof ký số (ECDSA P-256) mà Payment gắn vào header x-dprental-proof của mỗi message
/// trên payment.analytics.v1. Logic kiểm chứng giữ nguyên của Bảo; khác ở chỗ trả <see cref="Result"/>
/// và phân loại lỗi:
/// - <see cref="ProofKeyUnknownCode"/>: keyId chưa có trong cấu hình (thường do Payment xoay key) → thử lại,
///   không dead letter, để không mất dữ liệu chỉ vì thiếu cấu hình;
/// - <see cref="ProofInvalidCode"/>: thiếu proof, chữ ký sai hoặc không khớp message → dead letter.
/// </summary>
public sealed class PaymentProofVerifier
{
    public const string HeaderName = "x-dprental-proof";
    public const string ProofInvalidCode = "ANALYTICS_PROOF_INVALID";
    public const string ProofKeyUnknownCode = "ANALYTICS_PROOF_KEY_UNKNOWN";
    private const int MaxProofLength = 16_384;

    private readonly AnalyticsKafkaOptions _options;
    private readonly TimeProvider _timeProvider;

    public PaymentProofVerifier(IOptions<AnalyticsKafkaOptions> options, TimeProvider timeProvider)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Result Verify(KafkaRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.ProofHeader) || record.ProofHeader.Length > MaxProofLength)
        {
            return Invalid("Thiếu proof của Payment.");
        }

        try
        {
            using JsonDocument proof = JsonDocument.Parse(record.ProofHeader);
            string signed = proof.RootElement.GetProperty("payload").GetString()!;
            string signature = proof.RootElement.GetProperty("signature").GetString()!;
            using JsonDocument body = JsonDocument.Parse(signed);
            JsonElement claims = body.RootElement;

            string keyId = claims.GetProperty("keyId").GetString()!;
            if (!_options.ProofPublicKeys.TryGetValue(keyId, out string? publicKey))
            {
                return Result.Failure(Error.Failure(ProofKeyUnknownCode, $"Chưa cấu hình public key '{keyId}' cho proof Payment."));
            }

            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            bool signatureValid = verifier.KeySize == 256 && verifier.VerifyData(
                Encoding.UTF8.GetBytes("DPRENTAL:PUBLICATION:V1:" + signed),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!signatureValid)
            {
                return Invalid("Chữ ký proof không hợp lệ.");
            }

            using JsonDocument envelope = JsonDocument.Parse(record.Value);
            JsonElement message = envelope.RootElement;
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(record.Value)));

            // Proof phải gắn đúng với message này: cùng topic, key, messageId, loại, nội dung (digest)
            bool bound =
                claims.GetProperty("schema").GetInt32() == 1 &&
                claims.GetProperty("issuer").GetString() == _options.ProofIssuer &&
                claims.GetProperty("environment").GetString() == _options.ProofEnvironment &&
                claims.GetProperty("audience").GetString() == "analytics-service" &&
                claims.GetProperty("topic").GetString() == record.Topic &&
                claims.GetProperty("messageKey").GetString() == record.Key &&
                claims.GetProperty("messageId").GetGuid() == message.GetProperty("messageId").GetGuid() &&
                claims.GetProperty("messageType").GetString() == message.GetProperty("messageType").GetString() &&
                claims.GetProperty("payloadDigest").GetString() == digest &&
                claims.GetProperty("eventId").GetGuid() != Guid.Empty &&
                claims.GetProperty("operationId").GetGuid() != Guid.Empty &&
                claims.GetProperty("issuedAt").GetDateTimeOffset() <= _timeProvider.GetUtcNow().AddSeconds(1);

            return bound ? Result.Success() : Invalid("Proof không khớp với message.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException
            or InvalidOperationException or CryptographicException or ArgumentException)
        {
            // Proof hoặc message hỏng định dạng: lỗi dữ liệu
            return Invalid("Proof hoặc message sai định dạng.");
        }
    }

    private static Result Invalid(string message) => Result.Failure(Error.Validation(ProofInvalidCode, message));
}
