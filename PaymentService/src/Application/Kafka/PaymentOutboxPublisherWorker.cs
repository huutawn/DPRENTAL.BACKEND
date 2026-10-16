#pragma warning disable CA1848, CA1873
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Kafka;

public sealed class PaymentOutboxPublisherWorker(
    IServiceScopeFactory scopes,
    IOptions<PaymentKafkaOptions> options,
    ILogger<PaymentOutboxPublisherWorker> logger,
    TimeProvider clock) : BackgroundService
{
    private readonly PaymentKafkaOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)), clock);
        do
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
                logger.LogWarning(ex, "Payment outbox publish cycle failed; pending events will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return 0;
        var published = 0;
        for (var i = 0; i < Math.Max(1, _options.BatchSize); i++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentServiceDbContext>();
            var transport = scope.ServiceProvider.GetRequiredService<IEventTransport>();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            // One row lock per publish. A failed send rolls back and leaves the event pending.
            var message = (await db.OutboxMessages.FromSqlRaw(
                "SELECT m.* FROM outbox_messages m WHERE m.\"PublishedAt\" IS NULL " +
                "AND NOT EXISTS (SELECT 1 FROM outbox_messages earlier " +
                "WHERE earlier.\"AggregateId\" = m.\"AggregateId\" AND earlier.\"PublishedAt\" IS NULL " +
                "AND (earlier.\"CreatedAt\", earlier.\"Id\") < (m.\"CreatedAt\", m.\"Id\")) " +
                "ORDER BY m.\"CreatedAt\", m.\"Id\" FOR UPDATE OF m SKIP LOCKED LIMIT 1")
                .ToListAsync(cancellationToken)).SingleOrDefault();
            if (message is null) return published;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.DeliveryTimeoutSeconds)));
                await transport.PublishAsync(_options.PaymentEventsTopic,
                    message.AggregateId, message.Payload, timeout.Token);
                message.MarkPublished(clock.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                published++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not publish Payment outbox message {MessageId}; will retry.", message.MessageId);
                // Rollback on disposal. Stop this batch to avoid immediately retrying the same row.
                break;
            }
        }
        return published;
    }
}
