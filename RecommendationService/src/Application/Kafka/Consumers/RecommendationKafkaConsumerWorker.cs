using Confluent.Kafka;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.IServices;
using Dprental.RecommendationService.Application.Kafka.Options;
using Dprental.RecommendationService.Application.Kafka.Producer;
using Dprental.RecommendationService.Application.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.RecommendationService.Application.Kafka.Consumers;

public enum ConsumerSessionState
{
    WaitingForRecord,
    Processing,
    WaitingForRetry,
    WaitingForCommit,
    ClosingSession
}

public sealed record ProcessTaskResult(bool IsSuccess, Exception? Exception = null);

public sealed class RecommendationKafkaConsumerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaDlqProducer _dlqProducer;
    private readonly IKafkaClientFactory _clientFactory;
    private readonly KafkaOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecommendationKafkaConsumerWorker> _logger;

    public RecommendationKafkaConsumerWorker(
        IServiceScopeFactory scopeFactory,
        IKafkaDlqProducer dlqProducer,
        IKafkaClientFactory clientFactory,
        IOptions<KafkaOptions> options,
        ILogger<RecommendationKafkaConsumerWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _dlqProducer = dlqProducer;
        _clientFactory = clientFactory;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Kafka consumers are disabled in configuration.");
            return;
        }

        _logger.LogInformation(
            "Starting RecommendationKafkaConsumerWorker with single poll loops for Inventory ({InventoryTopic}) and Rental ({RentalTopic}).",
            _options.InventoryTopic, _options.RentalTopic);

        var inventoryTask = RunContinuousConsumerSessionAsync(
            _options.InventoryTopic,
            _options.InventoryConsumerGroup,
            _options.InventoryDlqTopic,
            InteractionEventSource.Inventory,
            stoppingToken);

        var rentalTask = RunContinuousConsumerSessionAsync(
            _options.RentalTopic,
            _options.RentalConsumerGroup,
            _options.RentalDlqTopic,
            InteractionEventSource.Rental,
            stoppingToken);

        await Task.WhenAll(inventoryTask, rentalTask);
    }

    private async Task RunContinuousConsumerSessionAsync(
        string topic,
        string consumerGroup,
        string dlqTopic,
        InteractionEventSource source,
        CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleSessionLoopAsync(topic, consumerGroup, dlqTopic, source, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Consumer session for {Group} on {Topic} encountered an unhandled error. Restarting session in 1s...", consumerGroup, topic);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunSingleSessionLoopAsync(
        string topic,
        string consumerGroup,
        string dlqTopic,
        InteractionEventSource source,
        CancellationToken stoppingToken)
    {
        long assignmentGeneration = 0;
        bool isSessionValid = true;

        IConsumer<string?, string>? consumer = null;
        try
        {
            consumer = _clientFactory.CreateConsumer(
                consumerGroup,
                $"{_options.ClientId}-{consumerGroup}",
                partitionsAssigned: (_, partitions) =>
                {
                    Interlocked.Increment(ref assignmentGeneration);
                    _logger.LogInformation("Consumer {Group} assigned {Count} partitions.", consumerGroup, partitions.Count);
                },
                partitionsRevoked: (_, partitionOffsets) =>
                {
                    Interlocked.Increment(ref assignmentGeneration);
                    isSessionValid = false;
                    _logger.LogInformation("Consumer {Group} revoked {Count} partitions. Invalidating session.", consumerGroup, partitionOffsets.Count);
                },
                partitionsLost: (_, partitionOffsets) =>
                {
                    Interlocked.Increment(ref assignmentGeneration);
                    isSessionValid = false;
                    _logger.LogWarning("Consumer {Group} lost {Count} partitions. Invalidating session.", consumerGroup, partitionOffsets.Count);
                },
                errorHandler: (_, error) =>
                {
                    if (error.IsFatal)
                    {
                        _logger.LogError("Fatal Kafka consumer error on {Topic}: {Reason}", topic, error.Reason);
                        isSessionValid = false;
                    }
                    else
                    {
                        _logger.LogDebug("Kafka notice on {Topic}: {Reason}", topic, error.Reason);
                    }
                });

            consumer.Subscribe(topic);
            _logger.LogInformation("Consumer {Group} subscribed to topic {Topic}.", consumerGroup, topic);

            var sessionState = ConsumerSessionState.WaitingForRecord;
            ConsumeResult<string?, string>? currentConsumeResult = null;
            long currentExpectedGeneration = 0;
            Task<ProcessTaskResult>? processingTask = null;
            CancellationTokenSource? inFlightCts = null;
            int retryAttempt = 0;
            DateTimeOffset retryDeadline = DateTimeOffset.MinValue;
            DateTimeOffset lastOutageLog = DateTimeOffset.MinValue;
            bool isRecoveredFromOutage = false;

            while (!stoppingToken.IsCancellationRequested && isSessionValid)
            {
                switch (sessionState)
                {
                    case ConsumerSessionState.WaitingForRecord:
                        {
                            var assignment = consumer.Assignment;
                            if (assignment != null && assignment.Count > 0)
                            {
                                consumer.Resume(assignment);
                            }

                            var cr = consumer.Consume(TimeSpan.FromMilliseconds(200));
                            if (cr == null || cr.IsPartitionEOF || cr.Message == null)
                            {
                                break;
                            }

                            // Immediately pause all owned partitions
                            if (assignment != null && assignment.Count > 0)
                            {
                                consumer.Pause(assignment);
                            }

                            currentConsumeResult = cr;
                            currentExpectedGeneration = Interlocked.Read(ref assignmentGeneration);
                            retryAttempt = 0;
                            inFlightCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                            processingTask = ProcessRecordTaskAsync(cr, source, dlqTopic, inFlightCts.Token);
                            sessionState = ConsumerSessionState.Processing;
                            break;
                        }

                    case ConsumerSessionState.Processing:
                        {
                            // Poll short timeout during in-flight processing to keep heartbeat alive
                            var unexpectedCr = consumer.Consume(TimeSpan.FromMilliseconds(100));
                            if (unexpectedCr != null && !unexpectedCr.IsPartitionEOF && unexpectedCr.Message != null)
                            {
                                _logger.LogWarning("Unexpected record received while processing. Closing session to recover.");
                                isSessionValid = false;
                                sessionState = ConsumerSessionState.ClosingSession;
                                break;
                            }

                            if (!isSessionValid)
                            {
                                inFlightCts?.Cancel();
                                sessionState = ConsumerSessionState.ClosingSession;
                                break;
                            }

                            if (processingTask!.IsCompleted)
                            {
                                var taskOutcome = await processingTask;
                                if (taskOutcome.IsSuccess)
                                {
                                    if (isSessionValid && currentExpectedGeneration == Interlocked.Read(ref assignmentGeneration))
                                    {
                                        if (isRecoveredFromOutage)
                                        {
                                            _logger.LogInformation("Recovered from outage. Processing resumed successfully for {Topic}.", topic);
                                            isRecoveredFromOutage = false;
                                        }
                                        sessionState = ConsumerSessionState.WaitingForCommit;
                                    }
                                    else
                                    {
                                        sessionState = ConsumerSessionState.ClosingSession;
                                    }
                                }
                                else
                                {
                                    // Failure: Schedule retry with exponential backoff & jitter
                                    retryAttempt++;
                                    int attemptInRound = ((retryAttempt - 1) % KafkaBackoffCalculator.MaxAttemptsPerRound) + 1;
                                    int round = ((retryAttempt - 1) / KafkaBackoffCalculator.MaxAttemptsPerRound) + 1;

                                    if (_timeProvider.GetUtcNow() - lastOutageLog > TimeSpan.FromSeconds(10))
                                    {
                                        if (taskOutcome.Exception != null)
                                        {
                                            _logger.LogWarning(
                                                taskOutcome.Exception,
                                                "Transient error processing {Topic}:{Partition}@{Offset} (Attempt {Attempt}/5, Round {Round}). Retrying...",
                                                topic, currentConsumeResult!.Partition.Value, currentConsumeResult.Offset.Value, attemptInRound, round);
                                        }
                                        lastOutageLog = _timeProvider.GetUtcNow();
                                        isRecoveredFromOutage = true;
                                    }

                                    var delay = KafkaBackoffCalculator.CalculateDelay(attemptInRound);
                                    retryDeadline = _timeProvider.GetUtcNow() + delay;
                                    sessionState = ConsumerSessionState.WaitingForRetry;
                                }
                            }
                            break;
                        }

                    case ConsumerSessionState.WaitingForRetry:
                        {
                            consumer.Consume(TimeSpan.FromMilliseconds(100));
                            if (!isSessionValid)
                            {
                                inFlightCts?.Cancel();
                                sessionState = ConsumerSessionState.ClosingSession;
                                break;
                            }

                            if (_timeProvider.GetUtcNow() >= retryDeadline)
                            {
                                inFlightCts?.Dispose();
                                inFlightCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                processingTask = ProcessRecordTaskAsync(currentConsumeResult!, source, dlqTopic, inFlightCts.Token);
                                sessionState = ConsumerSessionState.Processing;
                            }
                            break;
                        }

                    case ConsumerSessionState.WaitingForCommit:
                        {
                            try
                            {
                                consumer.Commit(currentConsumeResult!);
                                // Offset committed!
                                currentConsumeResult = null;
                                processingTask = null;
                                inFlightCts?.Dispose();
                                inFlightCts = null;
                                sessionState = ConsumerSessionState.WaitingForRecord;
                            }
                            catch (KafkaException ex)
                            {
                                if (isSessionValid && currentExpectedGeneration == Interlocked.Read(ref assignmentGeneration))
                                {
                                    _logger.LogWarning(
                                        ex,
                                        "Commit failed temporarily for {Topic}:{Partition}@{Offset}. Retrying commit...",
                                        topic, currentConsumeResult!.Partition.Value, currentConsumeResult.Offset.Value);
                                    consumer.Consume(TimeSpan.FromMilliseconds(100));
                                }
                                else
                                {
                                    sessionState = ConsumerSessionState.ClosingSession;
                                }
                            }
                            break;
                        }

                    case ConsumerSessionState.ClosingSession:
                        {
                            isSessionValid = false;
                            break;
                        }
                }
            }

            // Cleanup in-flight task on session close
            if (inFlightCts != null)
            {
                try
                {
                    inFlightCts.Cancel();
                    if (processingTask != null)
                    {
                        await Task.WhenAny(processingTask, Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, CancellationToken.None));
                    }
                }
                catch { }
                finally
                {
                    inFlightCts.Dispose();
                }
            }
        }
        finally
        {
            if (consumer != null)
            {
                try
                {
                    consumer.Close();
                    consumer.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing consumer for {Group}.", consumerGroup);
                }
            }
        }
    }

    private async Task<ProcessTaskResult> ProcessRecordTaskAsync(
        ConsumeResult<string?, string> consumeResult,
        InteractionEventSource source,
        string dlqTopic,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ingestionService = scope.ServiceProvider.GetRequiredService<IInteractionIngestionService>();

            string rawJson = consumeResult.Message.Value ?? string.Empty;
            var result = await ingestionService.ProcessEventAsync(rawJson, source, cancellationToken);

            if (result.Outcome is EventProcessOutcome.Processed
                or EventProcessOutcome.DuplicateIgnored
                or EventProcessOutcome.SkippedUnrelated)
            {
                return new ProcessTaskResult(true);
            }

            if (result.Outcome is EventProcessOutcome.ConflictMismatch
                or EventProcessOutcome.SchemaValidationError)
            {
                await _dlqProducer.ProduceToDlqAsync(
                    dlqTopic,
                    consumeResult.Message.Key,
                    rawJson,
                    result.ErrorReason ?? "Payload mismatch or schema validation failure.",
                    consumeResult.Topic,
                    consumeResult.Partition.Value,
                    consumeResult.Offset.Value,
                    cancellationToken);

                return new ProcessTaskResult(true);
            }

            return new ProcessTaskResult(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProcessTaskResult(false, ex);
        }
    }
}
