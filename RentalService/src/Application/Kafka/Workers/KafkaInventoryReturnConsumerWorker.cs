#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Confluent.Kafka;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RentalService.Application.Kafka.Workers;

/// <summary>Commits a result offset only after Rental's assignment updates are durable.</summary>
public sealed class KafkaInventoryReturnConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<KafkaInventoryReturnConsumerWorker> _logger;
    private readonly KafkaOptions _options;
    private readonly bool _active;
    private readonly TimeProvider _clock;

    public KafkaInventoryReturnConsumerWorker(IServiceScopeFactory scopes,
        IOptions<KafkaOptions> options, IConfiguration configuration,
        ILogger<KafkaInventoryReturnConsumerWorker> logger, TimeProvider clock)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
        _clock = clock;
        _active = _options.Enabled && configuration.GetValue("Rental:InventoryReturn:Enabled", true) && string.Equals(
            configuration["Rental:InventoryReturn:Transport"], "Kafka", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_active) return;
        var config = new ConsumerConfig
        {
            BrokerAddressFamily = BrokerAddressFamily.V4,
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.InventoryReturnConsumerGroupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };
        if (_options.SecurityProtocol.HasValue) config.SecurityProtocol = _options.SecurityProtocol;
        if (_options.SaslMechanism.HasValue) config.SaslMechanism = _options.SaslMechanism;
        if (!string.IsNullOrWhiteSpace(_options.SaslUsername)) config.SaslUsername = _options.SaslUsername;
        if (!string.IsNullOrWhiteSpace(_options.SaslPassword)) config.SaslPassword = _options.SaslPassword;
        if (!string.IsNullOrWhiteSpace(_options.SslCaLocation)) config.SslCaLocation = _options.SslCaLocation;
        if (!string.IsNullOrWhiteSpace(_options.SslCertificateLocation)) config.SslCertificateLocation = _options.SslCertificateLocation;
        if (!string.IsNullOrWhiteSpace(_options.SslKeyLocation)) config.SslKeyLocation = _options.SslKeyLocation;

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.InventoryReturnEventsTopic);
        while (!stoppingToken.IsCancellationRequested)
        {
            TopicPartitionOffset? currentOffset = null;
            try
            {
                var message = consumer.Consume(TimeSpan.FromMilliseconds(_options.ConsumeTimeoutMilliseconds));
                if (message?.Message?.Value is not { } json) continue;
                currentOffset = message.TopicPartitionOffset;
                string? msgType = null;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("messageType", out var type) &&
                        type.ValueKind == JsonValueKind.String)
                    {
                        msgType = type.GetString();
                        if (msgType != KafkaContractSpec.EventTypes.ReturnedUnitsApplied &&
                            msgType != KafkaContractSpec.EventTypes.ReturnedUnitsRejected)
                        {
                            consumer.Commit(message);
                            continue;
                        }
                    }
                }
                catch (JsonException) { /* Handler routes malformed input to the durable DLQ. */ }
                using var scope = _scopes.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<InventoryReturnResultHandler>();
                var applied = string.Equals(msgType, KafkaContractSpec.EventTypes.ReturnedUnitsRejected, StringComparison.OrdinalIgnoreCase)
                    ? await handler.RejectAsync(json, stoppingToken)
                    : await handler.ApplyAsync(json, stoppingToken);
                if (applied.IsFailure)
                {
                    _logger.LogWarning("Inventory return result at {Offset} is not applied: {Code}",
                        message.TopicPartitionOffset, applied.Error.Code);
                    if (applied.Error.Type is ErrorType.Failure or ErrorType.ServiceUnavailable)
                    {
                        consumer.Seek(message.TopicPartitionOffset);
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        continue;
                    }
                    var writer = scope.ServiceProvider.GetRequiredService<IKafkaOutboxWriter>();
                    var deadLetterId = OutboxMessageIdGenerator.ComputeDeterministicId(
                        KafkaContractSpec.Producers.RentalService, "InventoryReturnDlq",
                        $"{message.Topic}:{message.Partition.Value}:{message.Offset.Value}");
                    var utcNow = _clock.GetUtcNow();
                    var deadLetter = new KafkaMessageEnvelope<DeadLetterPayload>(
                        deadLetterId, KafkaContractSpec.EventTypes.DeadLetter,
                        KafkaContractSpec.CurrentEnvelopeVersion, KafkaContractSpec.Producers.RentalService,
                        deadLetterId.ToString("D"), utcNow, null, null,
                        new DeadLetterPayload(KafkaContractSpec.EventTypes.ReturnedUnitsApplied,
                            json, applied.Error.Code, utcNow));
                    var queued = await writer.EnqueueAsync(_options.InventoryReturnDlqTopic,
                        deadLetterId.ToString("D"), deadLetter, stoppingToken);
                    if (queued.IsFailure)
                    {
                        consumer.Seek(message.TopicPartitionOffset);
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                        continue;
                    }
                }
                consumer.Commit(message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inventory return consumer will retry from its committed offset.");
                if (currentOffset is not null)
                {
                    try { consumer.Seek(currentOffset); }
                    catch (KafkaException seekError)
                    {
                        _logger.LogWarning(seekError, "Could not seek Inventory result; restart consumer from committed offset.");
                        throw;
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        consumer.Close();
    }
}
