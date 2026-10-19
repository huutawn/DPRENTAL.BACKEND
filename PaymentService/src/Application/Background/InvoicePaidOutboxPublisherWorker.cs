#pragma warning disable CA1848, CA1873
using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Application.Kafka;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Domain.IRepository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Common.Cryptography;

namespace Dprental.PaymentService.Application.Background;

public sealed class InvoicePaidOutboxPublisherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<PaymentKafkaOptions> _options;
    private readonly ILogger<InvoicePaidOutboxPublisherWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _workerId;

    public InvoicePaidOutboxPublisherWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<PaymentKafkaOptions> options,
        ILogger<InvoicePaidOutboxPublisherWorker> logger,
        TimeProvider? timeProvider = null,
        string? workerId = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _workerId = workerId ?? $"{Environment.MachineName}_{Guid.NewGuid():N}";
    }

    public string WorkerId => _workerId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = _options.CurrentValue;
        if (!config.Enabled)
        {
            _logger.LogInformation("InvoicePaidOutboxPublisherWorker is disabled via configuration.");
            return;
        }

        _logger.LogInformation("InvoicePaidOutboxPublisherWorker started with worker ID {WorkerId}, polling interval {Interval}s",
            _workerId, config.PollingIntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(1, config.PollingIntervalSeconds));
        using var timer = new PeriodicTimer(interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingMessagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in InvoicePaidOutboxPublisherWorker processing cycle.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("InvoicePaidOutboxPublisherWorker stopped.");
    }

    public async Task<int> ProcessPendingMessagesAsync(CancellationToken cancellationToken)
    {
        var config = _options.CurrentValue;
        if (!config.Enabled)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var producer = scope.ServiceProvider.GetRequiredService<IInvoicePaidKafkaProducer>();
        var journalRepo = scope.ServiceProvider.GetService<ITransactionJournalRepository>();

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var leaseDuration = TimeSpan.FromSeconds(Math.Max(5, config.LeaseDurationSeconds));

        var claimedMessages = await outboxRepo.ClaimPendingBatchAsync(
            _workerId,
            utcNow,
            leaseDuration,
            config.BatchSize,
            config.MaxRetries,
            cancellationToken);

        if (claimedMessages.Count == 0)
        {
            return 0;
        }

        int publishedCount = 0;
        foreach (var message in claimedMessages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            string topic = !string.IsNullOrWhiteSpace(config.Topic) ? config.Topic : "payment.events.v1";
            // AggregateId holds the RentalOrderId
            string key = message.AggregateId;

            TransactionJournalEntry? journalEntry = null;
            string digest = CanonicalJournalHasher.ComputeOutboxDigest(message.Payload);

            if (journalRepo is not null)
            {
                if (message.EventId.HasValue)
                {
                    journalEntry = await journalRepo.GetByEventIdAsync(message.EventId.Value, cancellationToken);
                }
                if (journalEntry is null)
                {
                    journalEntry = await journalRepo.GetByOutboxDigestAsync(digest, cancellationToken);
                }
            }

            if (journalEntry is null)
            {
                _logger.LogWarning("Outbox message {MessageId} has no corresponding journal entry; cannot publish without evidence proof.", message.MessageId);
                message.RecordAttemptFailure("AWAITING_JOURNAL_ENTRY", utcNow, utcNow.AddSeconds(5));
                continue;
            }

            if (!journalEntry.IsConfirmed || string.IsNullOrWhiteSpace(journalEntry.ProofSignature))
            {
                _logger.LogInformation("Outbox message {MessageId} awaiting integrity confirmation before publish; releasing lease.", message.MessageId);
                message.ReleaseClaim();
                continue;
            }

            string versionedProof = System.Text.Json.JsonSerializer.Serialize(new
            {
                v = 2,
                msgId = message.MessageId,
                evtId = journalEntry.EventId,
                opId = journalEntry.OperationId,
                stream = journalEntry.StreamId,
                ver = journalEntry.Version,
                entryHash = journalEntry.EntryHash,
                digest = digest,
                keyId = journalEntry.SigningKeyId,
                sig = journalEntry.ProofSignature,
                confirmedAt = journalEntry.ConfirmedAt
            });

            var headers = new Dictionary<string, string>
            {
                ["x-dprental-signature"] = journalEntry.ProofSignature,
                ["x-dprental-key-id"] = journalEntry.SigningKeyId ?? string.Empty,
                ["x-dprental-proof"] = versionedProof,
                ["x-dprental-stream-id"] = journalEntry.StreamId,
                ["x-dprental-stream-version"] = journalEntry.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["x-dprental-event-id"] = journalEntry.EventId.ToString("D")
            };

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.DeliveryTimeoutSeconds)));

                await producer.ProduceAsync(topic, key, message.Payload, headers, cts.Token);

                DateTimeOffset ackNow = _timeProvider.GetUtcNow();
                bool marked = await outboxRepo.MarkPublishedAsync(message.Id, _workerId, ackNow, cancellationToken);
                if (marked)
                {
                    publishedCount++;
                    _logger.LogInformation("Successfully published outbox message {MessageId} to {Topic}", message.MessageId, topic);
                }
                else
                {
                    _logger.LogWarning("Lease expired or lost before marking published for outbox message {MessageId}", message.MessageId);
                }
            }
            catch (Exception ex)
            {
                DateTimeOffset failNow = _timeProvider.GetUtcNow();
                int attempt = message.RetryCount;
                int baseDelay = Math.Min(config.MaxBackoffSeconds, (int)Math.Pow(2, Math.Min(attempt, 6)));
                int jitter = Random.Shared.Next(0, Math.Max(1, baseDelay / 4));
                var nextRetryAt = failNow.AddSeconds(baseDelay + jitter);

                bool isExhausted = attempt >= config.MaxRetries;
                if (isExhausted)
                {
                    _logger.LogError(ex, "Outbox message {MessageId} for order {AggregateId} exceeded max retries {MaxRetries}. Retained for investigation/replay. Error: {Error}",
                        message.MessageId, message.AggregateId, config.MaxRetries, ex.Message);
                }
                else
                {
                    _logger.LogWarning(ex, "Failed to publish Outbox message {MessageId} (attempt {Attempt}/{MaxRetries}). Next retry at {NextRetryAt}",
                        message.MessageId, attempt, config.MaxRetries, nextRetryAt);
                }

                await outboxRepo.RecordFailureAsync(message.Id, _workerId, ex.Message, failNow, nextRetryAt, isExhausted, cancellationToken);
            }
        }

        return publishedCount;
    }
}
