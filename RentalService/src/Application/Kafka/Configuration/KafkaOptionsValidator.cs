using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Configuration;

public sealed class KafkaOptionsValidator : IValidateOptions<KafkaOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // When Kafka is not enabled, configuration is not enforced
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
        {
            failures.Add("BootstrapServers is required when Kafka is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.ConsumerGroupId))
        {
            failures.Add("ConsumerGroupId is required when Kafka is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.RentalEventsTopic))
        {
            failures.Add("RentalEventsTopic cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.PaymentEventsTopic))
        {
            failures.Add("PaymentEventsTopic cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.PaymentDlqTopic))
        {
            failures.Add("PaymentDlqTopic cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.InventoryCommandsTopic))
        {
            failures.Add("InventoryCommandsTopic cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.PaymentCommandsTopic))
        {
            failures.Add("PaymentCommandsTopic cannot be empty.");
        }

        if (options.DeliveryTimeoutSeconds <= 0)
        {
            failures.Add("DeliveryTimeoutSeconds must be greater than zero.");
        }

        if (options.LeaseDurationSeconds <= 0)
        {
            failures.Add("LeaseDurationSeconds must be greater than zero.");
        }

        if (options.MaxBatchPerRound <= 0)
        {
            failures.Add("MaxBatchPerRound must be greater than zero.");
        }

        if (options.OutboxPollingIntervalSeconds <= 0)
        {
            failures.Add("OutboxPollingIntervalSeconds must be greater than zero.");
        }

        if (options.InboxPollingIntervalSeconds <= 0)
        {
            failures.Add("InboxPollingIntervalSeconds must be greater than zero.");
        }

        if (options.ShutdownTimeoutSeconds <= 0)
        {
            failures.Add("ShutdownTimeoutSeconds must be greater than zero.");
        }

        if (options.ConsumeTimeoutMilliseconds <= 0)
        {
            failures.Add("ConsumeTimeoutMilliseconds must be greater than zero.");
        }

        // SASL / TLS checks
        if (options.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl)
        {
            if (options.SaslMechanism == null)
            {
                failures.Add("SaslMechanism is required when SASL security protocol is configured.");
            }

            if (string.IsNullOrWhiteSpace(options.SaslUsername))
            {
                failures.Add("SaslUsername is required when SASL security protocol is configured.");
            }

            if (string.IsNullOrWhiteSpace(options.SaslPassword))
            {
                failures.Add("SaslPassword is required when SASL security protocol is configured.");
            }
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
