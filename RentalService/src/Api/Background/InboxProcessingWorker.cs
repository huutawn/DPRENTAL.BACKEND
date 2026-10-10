#pragma warning disable CA1848, CA1873
using System.Text.Json;
using Dprental.RentalService.Application.DTOs.Integration;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.Integration;
using Dprental.RentalService.Application.Payments;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Background;

public sealed class InboxProcessingWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboxProcessingWorker> _logger;
    private readonly TimeSpan _pollingInterval;
    private readonly string _dlqTopic;

    public InboxProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InboxProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        int pollSeconds = configuration.GetValue("Workers:InboxProcessor:PollingIntervalSeconds", 15);
        _pollingInterval = TimeSpan.FromSeconds(Math.Max(1, pollSeconds));
        _dlqTopic = configuration["Kafka:PaymentDlqTopic"] ?? "payment.events.v1.dlq";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InboxProcessingWorker started with polling interval {Interval}s", _pollingInterval.TotalSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in InboxProcessingWorker loop");
            }

            try
            {
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("InboxProcessingWorker stopping");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var inboxRepository = scope.ServiceProvider.GetRequiredService<IInboxRepository>();
        var outboxRepository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var paymentWorkflow = scope.ServiceProvider.GetRequiredService<IPaymentWorkflowService>();
        var refundConsumer = scope.ServiceProvider.GetRequiredService<IRefundCompletedConsumer>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        DateTimeOffset utcNow = DateTimeOffset.UtcNow;

        var messages = await inboxRepository.GetPendingRetryBatchAsync(utcNow, 50, cancellationToken);
        if (messages.Count == 0)
        {
            return 0;
        }

        _logger.LogInformation("InboxProcessingWorker processing {Count} pending inbox messages", messages.Count);

        int processed = 0;
        foreach (var msg in messages)
        {
            try
            {
                bool success = false;
                string? failureCode = null;
                string? failureMsg = null;

                if (string.Equals(msg.MessageType, "InvoicePaid", StringComparison.OrdinalIgnoreCase))
                {
                    var envelope = JsonSerializer.Deserialize<PaymentEventEnvelope<InvoicePaidPayload>>(msg.Payload, JsonOptions);
                    if (envelope != null)
                    {
                        var res = await paymentWorkflow.ProcessInvoicePaidAsync(envelope, cancellationToken);
                        success = res.IsSuccess;
                        if (!res.IsSuccess)
                        {
                            failureCode = res.Error.Code;
                            failureMsg = res.Error.Message;
                        }
                    }
                }
                else if (string.Equals(msg.MessageType, "RefundCompleted", StringComparison.OrdinalIgnoreCase))
                {
                    var envelope = JsonSerializer.Deserialize<PaymentEventEnvelope<RefundCompletedPayload>>(msg.Payload, JsonOptions);
                    if (envelope != null)
                    {
                        var res = await refundConsumer.ProcessRefundCompletedAsync(envelope, cancellationToken);
                        success = res.IsSuccess;
                        if (!res.IsSuccess)
                        {
                            failureCode = res.Error.Code;
                            failureMsg = res.Error.Message;
                        }
                    }
                }

                if (success)
                {
                    msg.MarkProcessed(DateTimeOffset.UtcNow);
                    inboxRepository.Update(msg);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    processed++;
                }
                else
                {
                    // Check if error is retryable or terminal
                    bool isRetryable = failureCode is "INVOICE_NOT_ATTACHED" or "ORDER_NOT_FOUND" or "Payment.Timeout" or "Payment.Unavailable";

                    if (isRetryable && msg.RetryCount < 9)
                    {
                        double baseDelay = Math.Min(60, Math.Pow(2, msg.RetryCount));
                        int jitterMs = Random.Shared.Next(0, 1000);
                        DateTimeOffset nextRetry = DateTimeOffset.UtcNow.AddSeconds(baseDelay).AddMilliseconds(jitterMs);

                        msg.MarkFailed(failureMsg ?? "Retryable error occurred", nextRetry);
                        inboxRepository.Update(msg);
                        await unitOfWork.SaveChangesAsync(cancellationToken);
                    }
                    else
                    {
                        // Terminal failure or exhausted retries -> route to DLQ
                        msg.MarkDeadLettered(failureMsg ?? "Max retries exceeded");
                        inboxRepository.Update(msg);

                        var dlqEnvelope = new RentalEventEnvelope<DeadLetterPayload>(
                            Guid.NewGuid(),
                            RentalEventTypes.DeadLetter,
                            1,
                            "rental-service",
                            msg.AggregateId,
                            DateTimeOffset.UtcNow,
                            msg.CorrelationId,
                            msg.CausationId,
                            new DeadLetterPayload(msg.MessageType, msg.Payload, $"{failureCode}: {failureMsg}", DateTimeOffset.UtcNow));

                        var outbox = new RentalOutboxMessage(
                            dlqEnvelope.MessageId,
                            _dlqTopic,
                            msg.AggregateId,
                            RentalEventTypes.DeadLetter,
                            JsonSerializer.Serialize(dlqEnvelope, JsonOptions),
                            DateTimeOffset.UtcNow);

                        await outboxRepository.AddAsync(outbox, cancellationToken);
                        await unitOfWork.SaveChangesAsync(cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi xử lý Inbox message {Id}", msg.Id);
            }
        }

        return processed;
    }
}
