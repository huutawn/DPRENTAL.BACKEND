using Dprental.PaymentService.Api.Tests.TestHelpers;
using Dprental.PaymentService.Application.Kafka;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public sealed class PaymentOutboxPublisherTests
{
    [Fact]
    public async Task Broker_failure_keeps_message_pending_then_ack_marks_it_published()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var dbOptions = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            var orderId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            await using (var db = new PaymentServiceDbContext(dbOptions))
            {
                await db.Database.MigrateAsync();
                db.OutboxMessages.Add(OutboxMessage.Create(messageId, "RentalSettled",
                    "RentalOrder", orderId.ToString("D"), "{\"messageType\":\"RentalSettled\"}",
                    DateTimeOffset.UtcNow));
                await db.SaveChangesAsync();
            }

            var transport = new RecordingTransport { Fail = true };
            using var services = new ServiceCollection()
                .AddDbContext<PaymentServiceDbContext>(options => options.UseNpgsql(connectionString))
                .AddSingleton<IEventTransport>(transport)
                .BuildServiceProvider();
            var worker = new PaymentOutboxPublisherWorker(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new PaymentKafkaOptions { Enabled = true, PaymentEventsTopic = "payment.events.v1" }),
                NullLogger<PaymentOutboxPublisherWorker>.Instance,
                TimeProvider.System);

            Assert.Equal(0, await worker.ProcessBatchAsync(CancellationToken.None));
            await using (var db = new PaymentServiceDbContext(dbOptions))
                Assert.Null((await db.OutboxMessages.SingleAsync()).PublishedAt);

            transport.Fail = false;
            Assert.Equal(1, await worker.ProcessBatchAsync(CancellationToken.None));
            Assert.Equal(0, await worker.ProcessBatchAsync(CancellationToken.None));
            Assert.Equal("payment.events.v1", transport.Topic);
            Assert.Equal(orderId.ToString("D"), transport.Key);
            await using (var db = new PaymentServiceDbContext(dbOptions))
                Assert.NotNull((await db.OutboxMessages.SingleAsync()).PublishedAt);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private sealed class RecordingTransport : IEventTransport
    {
        public bool Fail { get; set; }
        public string? Topic { get; private set; }
        public string? Key { get; private set; }

        public Task PublishAsync(string topic, string partitionKey, string payload,
            CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("broker unavailable");
            Topic = topic;
            Key = partitionKey;
            return Task.CompletedTask;
        }
    }
}
