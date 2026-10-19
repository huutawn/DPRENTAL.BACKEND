using Dprental.PaymentService.Api.Tests.TestHelpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dprental.PaymentService.Application.Background;
using Dprental.PaymentService.Application.Kafka;
using Dprental.PaymentService.Application.Options;
using Dprental.PaymentService.Application.Services;
using Dprental.PaymentService.Domain.Entities;
using Dprental.PaymentService.Domain.IRepository;
using Dprental.PaymentService.Domain.Persistence;
using Dprental.PaymentService.Domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Dprental.PaymentService.Api.Tests.Integration;

public class InvoicePaidOutboxPublisherTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeKafkaProducer : IInvoicePaidKafkaProducer
    {
        public ConcurrentBag<(string Topic, string Key, string Payload)> ProducedMessages { get; } = new();
        public Func<string, string, string, Task>? OnProduce { get; set; }

        public async Task ProduceAsync(string topic, string key, string payload, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
        {
            if (OnProduce != null)
            {
                await OnProduce(topic, key, payload);
            }
            ProducedMessages.Add((topic, key, payload));
        }
    }

    private static (IServiceProvider Provider, FakeKafkaProducer Producer, FakeTimeProvider TimeProvider) BuildTestServices(
        string connString,
        FakeTimeProvider timeProvider,
        PaymentKafkaOptions? kafkaOptions = null,
        FakeKafkaProducer? producer = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<PaymentServiceDbContext>(options => options.UseNpgsql(connString));
        services.AddScoped<IOutboxRepository, EfOutboxRepository>();
        services.AddScoped<ITransactionJournalRepository, EfTransactionJournalRepository>();

        var fakeProducer = producer ?? new FakeKafkaProducer();
        services.AddSingleton<IInvoicePaidKafkaProducer>(fakeProducer);

        var optionsMonitor = new TestOptionsMonitor<PaymentKafkaOptions>(kafkaOptions ?? new PaymentKafkaOptions
        {
            BootstrapServers = "localhost:29092",
            Topic = "payment.events.v1",
            DeliveryTimeoutSeconds = 10,
            LeaseDurationSeconds = 30,
            PollingIntervalSeconds = 15,
            MaxRetries = 10,
            MaxBackoffSeconds = 60,
            BatchSize = 10,
            Enabled = true
        });
        services.AddSingleton<IOptionsMonitor<PaymentKafkaOptions>>(optionsMonitor);
        services.AddSingleton<TimeProvider>(timeProvider);

        return (services.BuildServiceProvider(), fakeProducer, timeProvider);
    }

    private static void AddConfirmedJournal(PaymentServiceDbContext ctx, OutboxMessage outbox)
    {
        var journal = JournalEntryFactory.CreateEntry(
            operationId: Guid.NewGuid(),
            streamId: $"STREAM-{outbox.MessageId}",
            streamType: JournalStreamTypes.Invoice,
            version: 1,
            eventId: Guid.NewGuid(),
            eventType: JournalEventTypes.InvoiceCreated,
            amount: 1_000_000m,
            currency: "VND",
            status: "PAID",
            confirmationSource: JournalConfirmationSources.System,
            actor: "SYSTEM",
            timestampUtc: FixedNow.AddMinutes(-5),
            previousHash: CanonicalJournalHasher.GenesisHash,
            encryptor: new PaymentDataEncryptor(new byte[32]),
            outboxDigest: CanonicalJournalHasher.ComputeOutboxDigest(outbox.Payload),
            outboxMessageId: outbox.MessageId);

        journal.Confirm("sig-test", "key-test", FixedNow.AddMinutes(-4));
        ctx.TransactionJournals.Add(journal);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        public TestOptionsMonitor(T currentValue) => CurrentValue = currentValue;
        public T CurrentValue { get; set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task Publisher_delivers_rental_settled_after_earlier_invoice_paid_for_same_order()
    {
        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connectionString);
            var orderId = Guid.NewGuid();
            var now = FixedNow;
            await using (var db = new PaymentServiceDbContext(options))
            {
                await db.Database.MigrateAsync();
                var outbox1 = OutboxMessage.Create(Guid.NewGuid(), "InvoicePaid", "RentalOrder",
                    orderId.ToString("D"), "{\"messageType\":\"InvoicePaid\"}", now.AddMinutes(-1));
                var outbox2 = OutboxMessage.Create(Guid.NewGuid(), "RentalSettled", "RentalOrder",
                    orderId.ToString("D"), "{\"messageType\":\"RentalSettled\"}", now);
                db.OutboxMessages.Add(outbox1);
                db.OutboxMessages.Add(outbox2);
                AddConfirmedJournal(db, outbox1);
                AddConfirmedJournal(db, outbox2);
                await db.SaveChangesAsync();
            }

            var clock = new FakeTimeProvider(now);
            var (provider, producer, _) = BuildTestServices(connectionString, clock);
            using (provider as IDisposable)
            {
                var worker = new InvoicePaidOutboxPublisherWorker(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    provider.GetRequiredService<IOptionsMonitor<PaymentKafkaOptions>>(),
                    NullLogger<InvoicePaidOutboxPublisherWorker>.Instance, clock);

                Assert.Equal(1, await worker.ProcessPendingMessagesAsync(CancellationToken.None));
                Assert.Single(producer.ProducedMessages);
                Assert.Contains("InvoicePaid", producer.ProducedMessages.Single().Payload, StringComparison.Ordinal);
                Assert.Equal(1, await worker.ProcessPendingMessagesAsync(CancellationToken.None));
                Assert.Equal(2, producer.ProducedMessages.Count);
                Assert.All(producer.ProducedMessages, produced =>
                    Assert.Equal(orderId.ToString("D"), produced.Key));
                await using var verify = new PaymentServiceDbContext(options);
                Assert.Equal(2, await verify.OutboxMessages.CountAsync(x => x.PublishedAt != null));
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task ProcessPendingMessages_ClaimsAndPublishesWithOrderKey_AndMarksPublished()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            var timeProvider = new FakeTimeProvider(FixedNow);
            var (sp, fakeProducer, _) = BuildTestServices(connString, timeProvider);

            Guid orderId = Guid.NewGuid();
            Guid msgId = Guid.NewGuid();
            var outbox = OutboxMessage.Create(
                msgId, "InvoicePaid", "RentalOrder", orderId.ToString("D"),
                "{\"rentalOrderId\":\"" + orderId + "\",\"amount\":1000000}", FixedNow.AddMinutes(-5));

            await using (var ctx = new PaymentServiceDbContext(options))
            {
                ctx.OutboxMessages.Add(outbox);
                AddConfirmedJournal(ctx, outbox);
                await ctx.SaveChangesAsync();
            }

            var worker = new InvoicePaidOutboxPublisherWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<PaymentKafkaOptions>>(),
                NullLogger<InvoicePaidOutboxPublisherWorker>.Instance,
                timeProvider,
                "worker-test-1");

            int published = await worker.ProcessPendingMessagesAsync(CancellationToken.None);
            Assert.Equal(1, published);

            // Verify message published with key = RentalOrderId
            Assert.Single(fakeProducer.ProducedMessages);
            var publishedMsg = fakeProducer.ProducedMessages.First();
            Assert.Equal("payment.events.v1", publishedMsg.Topic);
            Assert.Equal(orderId.ToString("D"), publishedMsg.Key);

            // Verify DB state
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var row = await verifyCtx.OutboxMessages.SingleAsync(m => m.Id == outbox.Id);
                Assert.NotNull(row.PublishedAt);
                Assert.Null(row.LockedBy);
                Assert.Null(row.LockedUntilUtc);
                Assert.Equal(1, row.RetryCount);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task TwoWorkersConcurrent_ClaimDisjointBatchesViaSkipLocked_WithoutConflict()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            var timeProvider = new FakeTimeProvider(FixedNow);
            var (sp, fakeProducer, _) = BuildTestServices(connString, timeProvider);

            // Seed 4 outbox messages
            for (int i = 0; i < 4; i++)
            {
                Guid orderId = Guid.NewGuid();
                var msg = OutboxMessage.Create(
                    Guid.NewGuid(), "InvoicePaid", "RentalOrder", orderId.ToString("D"),
                    "{\"orderIndex\":" + i + "}", FixedNow.AddMinutes(-10 + i));
                await using var ctx = new PaymentServiceDbContext(options);
                ctx.OutboxMessages.Add(msg);
                AddConfirmedJournal(ctx, msg);
                await ctx.SaveChangesAsync();
            }

            var worker1 = new InvoicePaidOutboxPublisherWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<PaymentKafkaOptions>>(),
                NullLogger<InvoicePaidOutboxPublisherWorker>.Instance,
                timeProvider,
                "worker-concurrent-1");

            var worker2 = new InvoicePaidOutboxPublisherWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<PaymentKafkaOptions>>(),
                NullLogger<InvoicePaidOutboxPublisherWorker>.Instance,
                timeProvider,
                "worker-concurrent-2");

            // Run concurrently
            var task1 = worker1.ProcessPendingMessagesAsync(CancellationToken.None);
            var task2 = worker2.ProcessPendingMessagesAsync(CancellationToken.None);

            await Task.WhenAll(task1, task2);

            Assert.Equal(4, fakeProducer.ProducedMessages.Count);

            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var unpub = await verifyCtx.OutboxMessages.Where(m => m.PublishedAt == null).ToListAsync();
                Assert.Empty(unpub);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task BrokerFailure_IncrementsRetryAndSetsBackoff_AndExhaustedDoesNotPublishOrDelete()
    {
        var (connString, dbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        try
        {
            var options = PostgresTestDatabaseHelper.CreateOptions(connString);
            await using (var ctx = new PaymentServiceDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            var timeProvider = new FakeTimeProvider(FixedNow);
            var fakeProducer = new FakeKafkaProducer
            {
                OnProduce = (_, _, _) => throw new InvalidOperationException("Broker unreachable")
            };

            var (sp, _, _) = BuildTestServices(connString, timeProvider, producer: fakeProducer);

            Guid orderId = Guid.NewGuid();
            var outbox = OutboxMessage.Create(
                Guid.NewGuid(), "InvoicePaid", "RentalOrder", orderId.ToString("D"),
                "{\"payload\":\"failing\"}", FixedNow.AddMinutes(-5));

            await using (var ctx = new PaymentServiceDbContext(options))
            {
                ctx.OutboxMessages.Add(outbox);
                AddConfirmedJournal(ctx, outbox);
                await ctx.SaveChangesAsync();
            }

            var worker = new InvoicePaidOutboxPublisherWorker(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOptionsMonitor<PaymentKafkaOptions>>(),
                NullLogger<InvoicePaidOutboxPublisherWorker>.Instance,
                timeProvider,
                "worker-fail");

            // Attempt 1: fails, sets NextRetryAt
            int count1 = await worker.ProcessPendingMessagesAsync(CancellationToken.None);
            Assert.Equal(0, count1);

            DateTimeOffset nextRetry;
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var row = await verifyCtx.OutboxMessages.SingleAsync(m => m.Id == outbox.Id);
                Assert.Null(row.PublishedAt);
                Assert.Null(row.LockedBy);
                Assert.Equal(1, row.RetryCount);
                Assert.NotNull(row.NextRetryAt);
                Assert.Contains("Broker unreachable", row.LastErrorMessage);
                nextRetry = row.NextRetryAt.Value;
            }

            // Immediately retrying before NextRetryAt does not process
            int countBeforeRetry = await worker.ProcessPendingMessagesAsync(CancellationToken.None);
            Assert.Equal(0, countBeforeRetry);

            // Advance time past NextRetryAt and simulate 9 more retries up to 10
            for (int attempt = 2; attempt <= 10; attempt++)
            {
                timeProvider.Advance(TimeSpan.FromSeconds(120));
                await worker.ProcessPendingMessagesAsync(CancellationToken.None);
            }

            // Verify final state: RetryCount == 10, NOT deleted, NOT published, NextRetryAt is null (exhausted)
            await using (var verifyCtx = new PaymentServiceDbContext(options))
            {
                var finalRow = await verifyCtx.OutboxMessages.SingleAsync(m => m.Id == outbox.Id);
                Assert.Equal(10, finalRow.RetryCount);
                Assert.Null(finalRow.PublishedAt);
                Assert.Null(finalRow.LockedBy);
                Assert.Null(finalRow.NextRetryAt);
                Assert.Contains("Broker unreachable", finalRow.LastErrorMessage);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(dbName);
        }
    }
}
