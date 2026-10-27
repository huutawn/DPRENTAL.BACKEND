using System.Security.Cryptography;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Kafka.Configuration;

/// <summary>
/// Kiểm tra cấu hình Kafka lúc khởi động: sai thì ứng dụng không chạy, thay vì worker báo lỗi lúc đang chạy.
/// Tắt Kafka thì không kiểm tra gì.
/// </summary>
public sealed class AnalyticsKafkaOptionsValidator : IValidateOptions<AnalyticsKafkaOptions>
{
    public ValidateOptionsResult Validate(string? name, AnalyticsKafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
        {
            failures.Add("Kafka:BootstrapServers là bắt buộc khi bật Kafka.");
        }

        if (string.IsNullOrWhiteSpace(options.GroupId))
        {
            failures.Add("Kafka:GroupId là bắt buộc khi bật Kafka.");
        }

        if (string.IsNullOrWhiteSpace(options.RentalTopic) || string.IsNullOrWhiteSpace(options.PaymentTopic))
        {
            failures.Add("Kafka:RentalTopic và Kafka:PaymentTopic không được rỗng.");
        }
        else if (string.Equals(options.RentalTopic, options.PaymentTopic, StringComparison.Ordinal))
        {
            failures.Add("Kafka:RentalTopic và Kafka:PaymentTopic phải khác nhau.");
        }

        if (string.IsNullOrWhiteSpace(options.ProofIssuer) || string.IsNullOrWhiteSpace(options.ProofEnvironment))
        {
            failures.Add("Kafka:ProofIssuer và Kafka:ProofEnvironment là bắt buộc khi bật Kafka.");
        }

        if (options.ProofPublicKeys.Count == 0)
        {
            failures.Add("Cần ít nhất một Kafka:ProofPublicKeys để kiểm chứng message của Payment.");
        }

        foreach ((string keyId, string encodedKey) in options.ProofPublicKeys)
        {
            if (!IsP256PublicKey(encodedKey))
            {
                failures.Add($"Kafka:ProofPublicKeys:{keyId} phải là public key P-256 (SubjectPublicKeyInfo, base64).");
            }
        }

        if (options.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl &&
            (options.SaslMechanism is null ||
             string.IsNullOrWhiteSpace(options.SaslUsername) ||
             string.IsNullOrWhiteSpace(options.SaslPassword)))
        {
            failures.Add("SaslMechanism, SaslUsername và SaslPassword là bắt buộc khi SecurityProtocol dùng SASL.");
        }

        if (options.RetryInitialDelayMilliseconds <= 0
            || options.RetryMaxDelaySeconds <= 0
            || options.RetryMaxDelaySeconds * 1000L < options.RetryInitialDelayMilliseconds)
        {
            failures.Add("RetryInitialDelayMilliseconds và RetryMaxDelaySeconds phải > 0, max không nhỏ hơn initial.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsP256PublicKey(string encodedKey)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(encodedKey), out _);
            return key.KeySize == 256;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
