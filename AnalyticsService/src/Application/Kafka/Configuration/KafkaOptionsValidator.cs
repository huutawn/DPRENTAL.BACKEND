using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Application.Kafka.Configuration;

/// <summary>
/// Kiểm tra cấu hình Kafka lúc khởi động; cấu hình sai thì ứng dụng không chạy thay vì âm thầm tắt consumer.
/// </summary>
public sealed class KafkaOptionsValidator : IValidateOptions<KafkaOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
        {
            failures.Add("BootstrapServers là bắt buộc khi bật Kafka.");
        }

        if (string.IsNullOrWhiteSpace(options.ConsumerGroupId))
        {
            failures.Add("ConsumerGroupId là bắt buộc khi bật Kafka.");
        }

        if (string.IsNullOrWhiteSpace(options.RentalEventsTopic))
        {
            failures.Add("RentalEventsTopic không được rỗng.");
        }

        if (string.IsNullOrWhiteSpace(options.PaymentEventsTopic))
        {
            failures.Add("PaymentEventsTopic không được rỗng.");
        }

        if (string.Equals(options.RentalEventsTopic, options.PaymentEventsTopic, StringComparison.Ordinal))
        {
            failures.Add("RentalEventsTopic và PaymentEventsTopic phải khác nhau.");
        }

        if (options.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl &&
            (options.SaslMechanism is null ||
             string.IsNullOrWhiteSpace(options.SaslUsername) ||
             string.IsNullOrWhiteSpace(options.SaslPassword)))
        {
            failures.Add("SaslMechanism, SaslUsername và SaslPassword là bắt buộc khi SecurityProtocol dùng SASL.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
