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

        DateTimeOffset lastSessionErrorLog = DateTimeOffset.MinValue;
        int suppressedSessionErrorCount = 0;
        bool hasLoggedSessionError = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSingleSessionLoopAsync(topic, consumerGroup, dlqTopic, source, stoppingToken);

                if (hasLoggedSessionError)
                {
                    _logger.LogInformation("Consumer session for {Group} on {Topic} recovered and exited cleanly.", consumerGroup, topic);
                    hasLoggedSessionError = false;
                    suppressedSessionErrorCount = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var now = _timeProvider.GetUtcNow();
                if (!hasLoggedSessionError || now - lastSessionErrorLog >= TimeSpan.FromSeconds(10))
                {
                    if (suppressedSessionErrorCount > 0)
                    {
                        _logger.LogWarning("Consumer session for {Group} on {Topic} encountered an error after {Count} suppressed failures: {Message}",
                            consumerGroup, topic, suppressedSessionErrorCount, ex.Message);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Consumer session for {Group} on {Topic} encountered an unhandled error. Restarting session...", consumerGroup, topic);
                    }
                    lastSessionErrorLog = now;
                    hasLoggedSessionError = true;
                    suppressedSessionErrorCount = 0;
                }
                else
                {
                    suppressedSessionErrorCount++;
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
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
        Task<ProcessTaskResult>? processingTask = null;
        CancellationTokenSource? inFlightCts = null;

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
            int retryAttempt = 0;
            DateTimeOffset retryDeadline = DateTimeOffset.MinValue;
            DateTimeOffset lastOutageLog = DateTimeOffset.MinValue;
            bool isRecoveredFromOutage = false;
            DateTimeOffset lastCommitErrorLog = DateTimeOffset.MinValue;
            int suppressedCommitErrorCount = 0;
            bool isCommitErrorActive = false;

            while (!stoppingToken.IsCancellationRequested && isSessionValid)
            {
                switch (sessionState)
                {
                    case ConsumerSessionState.WaitingForRecord:
                        {
                            var cr = consumer.Consume(TimeSpan.FromMilliseconds(200));
                            if (cr == null || cr.IsPartitionEOF || cr.Message == null)
                            {
                                break;
                            }

                            if (!isSessionValid)
                            {
                                sessionState = ConsumerSessionState.ClosingSession;
                                break;
                            }

                            var currentAssignment = consumer.Assignment;
                            if (currentAssignment == null || !currentAssignment.Any(tp => tp.Topic == cr.Topic && tp.Partition == cr.Partition))
                            {
                                _logger.LogWarning("Record {Topic}:{Partition}@{Offset} does not belong to current assignment. Invalidating session.",
                                    cr.Topic, cr.Partition.Value, cr.Offset.Value);
                                isSessionValid = false;
                                sessionState = ConsumerSessionState.ClosingSession;
                                break;
                            }

                            // Immediately pause all owned partitions before starting ingestion
                            consumer.Pause(currentAssignment);

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
                            if (!PollWhileBusy(consumer, "Processing", TimeSpan.FromMilliseconds(100)))
                            {
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
                            else
                            {
                                await Task.Yield();
                            }
                            break;
                        }

                    case ConsumerSessionState.WaitingForRetry:
                        {
                            if (!PollWhileBusy(consumer, "WaitingForRetry", TimeSpan.FromMilliseconds(100)))
                            {
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
                            if (!isSessionValid || currentExpectedGeneration != Interlocked.Read(ref assignmentGeneration))
                            {
                                _logger.LogWarning("Session or assignment generation changed before commit ({Expected} vs {Current}). Invalidating session.",
                                    currentExpectedGeneration, Interlocked.Read(ref assignmentGeneration));
                                isSessionValid = false;
                                sessionState = ConsumerSessionState.ClosingSession;
                                break;
                            }

                            try
                            {
                                consumer.Commit(currentConsumeResult!);
                                if (isCommitErrorActive)
                                {
                                    _logger.LogInformation(
                                        "Commit succeeded after previous failures for {Topic}:{Partition}@{Offset}.",
                                        topic, currentConsumeResult!.Partition.Value, currentConsumeResult.Offset.Value);
                                    isCommitErrorActive = false;
                                    suppressedCommitErrorCount = 0;
                                }

                                // Only resume once current record has committed successfully
                                var currentAssignment = consumer.Assignment;
                                if (currentAssignment != null && currentAssignment.Count > 0)
                                {
                                    consumer.Resume(currentAssignment);
                                }

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
                                    var now = _timeProvider.GetUtcNow();
                                    if (!isCommitErrorActive || now - lastCommitErrorLog >= TimeSpan.FromSeconds(10))
                                    {
                                        if (suppressedCommitErrorCount > 0)
                                        {
                                            _logger.LogWarning(
                                                ex,
                                                "Commit failed temporarily for {Group} on {Topic}:{Partition}@{Offset} (suppressed {Count} repeated warnings). Retrying commit...",
                                                consumerGroup, topic, currentConsumeResult!.Partition.Value, currentConsumeResult.Offset.Value, suppressedCommitErrorCount);
                                        }
                                        else
                                        {
                                            _logger.LogWarning(
                                                ex,
                                                "Commit failed temporarily for {Group} on {Topic}:{Partition}@{Offset}. Retrying commit...",
                                                consumerGroup, topic, currentConsumeResult!.Partition.Value, currentConsumeResult.Offset.Value);
                                        }
                                        lastCommitErrorLog = now;
                                        isCommitErrorActive = true;
                                        suppressedCommitErrorCount = 0;
                                    }
                                    else
                                    {
                                        suppressedCommitErrorCount++;
                                    }

                                    if (!PollWhileBusy(consumer, "WaitingForCommit", TimeSpan.FromMilliseconds(100)))
                                    {
                                        isSessionValid = false;
                                        sessionState = ConsumerSessionState.ClosingSession;
                                    }
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
        }
        finally
        {
            try
            {
                await CleanupInFlightTaskAsync(processingTask, inFlightCts);
            }
            finally
            {
                DisposeConsumerSafely(consumer, consumerGroup);
            }
        }
    }


    private bool PollWhileBusy(
        IConsumer<string?, string> consumer,
        string currentStateName,
        TimeSpan timeout)
    {
        var unexpectedCr = consumer.Consume(timeout);
        if (unexpectedCr != null && !unexpectedCr.IsPartitionEOF && unexpectedCr.Message != null)
        {
            _logger.LogWarning(
                "Unexpected record received while in {State} on {Topic}:{Partition}@{Offset}. Invalidating session without committing to allow Kafka to replay.",
                currentStateName, unexpectedCr.Topic, unexpectedCr.Partition.Value, unexpectedCr.Offset.Value);
            return false;
        }

        return true;
    }

    private async Task CleanupInFlightTaskAsync(
        Task<ProcessTaskResult>? processingTask,
        CancellationTokenSource? inFlightCts)
    {
        if (inFlightCts == null)
        {
            return;
        }

        try
        {
            try
            {
                inFlightCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            if (processingTask != null)
            {
                try
                {
                    try
                    {
                        await processingTask.WaitAsync(TimeSpan.FromSeconds(1), _timeProvider);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("In-flight processing task did not complete within 1s timeout during session cleanup. Waiting for task to terminate before releasing session...");
                        await processingTask;
                    }

                    var outcome = await processingTask;
                    if (!outcome.IsSuccess && outcome.Exception != null && outcome.Exception is not OperationCanceledException)
                    {
                        _logger.LogWarning(outcome.Exception, "In-flight task completed with error during session cleanup.");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected cancellation
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Observed unhandled exception from in-flight task during session cleanup.");
                }
            }
        }
        finally
        {
            inFlightCts.Dispose();
        }
    }

    private void DisposeConsumerSafely(IConsumer<string?, string>? consumer, string consumerGroup)
    {
        if (consumer == null)
        {
            return;
        }

        try
        {
            consumer.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing consumer for {Group}.", consumerGroup);
        }
        finally
        {
            try
            {
                consumer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing consumer for {Group}.", consumerGroup);
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
