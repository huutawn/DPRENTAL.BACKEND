using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Dprental.AnalyticsService.Application.Events.Payment;
using Dprental.AnalyticsService.Application.Events.Rental;
using Microsoft.Extensions.Options;

namespace Dprental.AnalyticsService.Api.Background;

/// <summary>Commits Kafka offsets only after the corresponding database projection is durable.</summary>
public sealed class AnalyticsKafkaConsumerWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AnalyticsKafkaOptions> optionsAccessor,
    ILogger<AnalyticsKafkaConsumerWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> IntakeFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "AnalyticsIntakeFailed"),
            "Analytics event intake failed; offset remains uncommitted.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsAccessor.Value;
        if (!options.Enabled) return;
        if (string.IsNullOrWhiteSpace(options.BootstrapServers) || options.ProofPublicKeys.Count == 0)
            throw new InvalidOperationException("Analytics Kafka requires BootstrapServers and Payment proof public keys.");

        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.GroupId,
            ClientId = "analytics-dashboard",
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };
        if (options.SecurityProtocol.HasValue) config.SecurityProtocol = options.SecurityProtocol.Value;
        if (options.SaslMechanism.HasValue) config.SaslMechanism = options.SaslMechanism.Value;
        if (!string.IsNullOrWhiteSpace(options.SaslUsername)) config.SaslUsername = options.SaslUsername;
        if (!string.IsNullOrWhiteSpace(options.SaslPassword)) config.SaslPassword = options.SaslPassword;
        if (!string.IsNullOrWhiteSpace(options.SslCaLocation)) config.SslCaLocation = options.SslCaLocation;
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe([options.RentalTopic, options.PaymentTopic]);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? current = null;
            try
            {
                var message = consumer.Consume(TimeSpan.FromMilliseconds(500));
                current = message;
                if (message?.Message?.Value is null) continue;

                if (message.Topic == options.PaymentTopic)
                {
                    string? proof = message.Message.Headers?.TryGetLastBytes("x-dprental-proof", out var bytes) == true
                        ? Encoding.UTF8.GetString(bytes) : null;
                    PaymentAnalyticsProofVerifier.Verify(message.Message.Value, proof, message.Topic,
                        message.Message.Key, options);
                    using var document = JsonDocument.Parse(message.Message.Value);
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<PaymentAnalyticsEventIngestor>()
                        .IngestAsync(document.RootElement, stoppingToken);
                }
                else if (message.Topic == options.RentalTopic)
                {
                    using var document = JsonDocument.Parse(message.Message.Value);
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<RentalEventIngestor>()
                        .IngestAsync(document.RootElement, stoppingToken);
                }

                consumer.Commit(message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IntakeFailed(logger, ex);
                if (current is not null)
                {
                    consumer.Seek(current.TopicPartitionOffset);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        consumer.Close();
    }
}
