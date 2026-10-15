using System.Net.Sockets;
using System.Text.Json;
using Confluent.Kafka;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.IServices.Integration;
using Dprental.RentalService.Application.IServices.Payments;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Consumers;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

public sealed class KafkaRealBrokerIntegrationTests
{
    private const string EnvKafkaBootstrapServers = "RENTAL_TEST_KAFKA_BOOTSTRAP_SERVERS";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static string? GetConfiguredBroker()
    {
        return Environment.GetEnvironmentVariable(EnvKafkaBootstrapServers);
    }

    private static async Task<bool> CanConnectToBrokerAsync(string bootstrapServers)
    {
        try
        {
            var hostPort = bootstrapServers.Split(',')[0].Trim();
            var parts = hostPort.Split(':');
            string host = parts[0];
            int port = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 9092;

            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var delayTask = Task.Delay(2000);
            if (await Task.WhenAny(connectTask, delayTask) == connectTask && client.Connected)
            {
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task RealKafkaBrokerAndPostgreSql_PublishConsumeRedelivery_EndToEnd()
    {
        string? bootstrapServers = GetConfiguredBroker();
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            // Skipped because RENTAL_TEST_KAFKA_BOOTSTRAP_SERVERS is not configured.
            return;
        }

        if (!await CanConnectToBrokerAsync(bootstrapServers))
        {
            Assert.Fail($"Kafka broker is configured via RENTAL_TEST_KAFKA_BOOTSTRAP_SERVERS='{bootstrapServers}' but could not be reached.");
            return;
        }

        if (!await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync())
        {
            throw new InvalidOperationException("PostgreSQL is not available for real integration test.");
        }

        var (connectionString, databaseName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();

        try
        {
            await using var db = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            await db.Database.MigrateAsync();

            Guid orderId = Guid.NewGuid();
            Guid messageId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            var payload = new InvoicePaidPayload(
                RentalOrderId: orderId,
                RenterId: Guid.NewGuid(),
                InvoiceRef: "INV-REAL-001",
                PaymentRef: "PAY-REAL-001",
                PaidAmount: 2_500_000m,
                Currency: "VND",
                PaidAt: now);

            var envelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
                messageId,
                KafkaContractSpec.EventTypes.InvoicePaid,
                KafkaContractSpec.CurrentEnvelopeVersion,
                KafkaContractSpec.Producers.PaymentService,
                orderId.ToString("D"),
                now,
                "corr-real-001",
                null,
                payload);

            string messageJson = JsonSerializer.Serialize(envelope, JsonOptions);

            string testTopic = $"test-payment-events-{Guid.NewGuid():N}";
            string testGroup = $"rental-test-group-{Guid.NewGuid():N}";

            var kafkaOptions = new KafkaOptions
            {
                Enabled = true,
                BootstrapServers = bootstrapServers,
                PaymentEventsTopic = testTopic,
                ConsumerGroupId = testGroup,
                DeliveryTimeoutSeconds = 10,
                ConsumeTimeoutMilliseconds = 500
            };
            // 1. Publish to real Kafka broker via KafkaProducerTransport
            using var transport = new KafkaProducerTransport(
                Options.Create(kafkaOptions),
                NullLogger<KafkaProducerTransport>.Instance);

            var deliveryResult = await transport.ProduceAsync(
                testTopic,
                orderId.ToString("D"),
                messageJson,
                CancellationToken.None);

            Assert.Equal(PersistenceStatus.Persisted, deliveryResult.Status);

            // 2. Setup DI scope for consumer worker
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddDbContext<RentalServiceDbContext>(options =>
                options.UseNpgsql(connectionString));
            services.AddScoped<IInboxRepository, InboxRepository>();
            services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
            services.AddScoped<IOrderLockManager, FakeOrderLockManager>();
            services.AddScoped<IOutboxRepository, OutboxRepository>();
            services.AddScoped<IKafkaOutboxWriter, KafkaOutboxWriter>();
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
            services.AddSingleton<IPaymentWorkflowService, StubPaymentWorkflow>();
            services.AddSingleton<IRefundCompletedConsumer, StubRefundConsumer>();
            services.AddScoped<IPaymentEventsDispatcher, PaymentEventsDispatcher>();
            services.AddSingleton(Options.Create(kafkaOptions));

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            // 3. Consume from real Kafka broker via KafkaPaymentEventsConsumerWorker
            using var consumerWorker = new KafkaPaymentEventsConsumerWorker(
                scopeFactory,
                Options.Create(kafkaOptions),
                NullLogger<KafkaPaymentEventsConsumerWorker>.Instance);

            // Poll by consuming from real broker
            RentalInboxMessage? inboxMessage = null;
            for (int i = 0; i < 40; i++)
            {
                await consumerWorker.ConsumeNextMessageAsync(CancellationToken.None);
                await using var checkDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
                inboxMessage = await checkDb.RentalInboxMessages.FirstOrDefaultAsync(m => m.MessageId == messageId);
                if (inboxMessage != null) break;
                await Task.Delay(100);
            }

            // 4. Verify durable persistence in PostgreSQL
            Assert.NotNull(inboxMessage);
            Assert.Equal(messageId, inboxMessage.MessageId);
            Assert.Equal(KafkaContractSpec.EventTypes.InvoicePaid, inboxMessage.MessageType);
            Assert.Equal("Received", inboxMessage.Status);
            Assert.Equal(testTopic, inboxMessage.KafkaTopic);
            Assert.NotNull(inboxMessage.KafkaOffset);

            // 5. Redelivery verification: Dispatch same message directly or consume again
            using (var directScope = scopeFactory.CreateScope())
            {
                var dispatcher = directScope.ServiceProvider.GetRequiredService<IPaymentEventsDispatcher>();
                var redeliveryResult = await dispatcher.ReceiveMessageAsync(messageJson, testTopic, inboxMessage.KafkaPartition, inboxMessage.KafkaOffset);
                Assert.True(redeliveryResult.IsSuccess); // Idempotent duplicate recognized
            }

            await using var verifyDb = new RentalServiceDbContext(PostgresTestDatabaseHelper.CreateOptions(connectionString));
            int totalInboxCount = await verifyDb.RentalInboxMessages.CountAsync(m => m.MessageId == messageId);
            Assert.Equal(1, totalInboxCount);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(databaseName);
        }
    }

    private sealed class StubPaymentWorkflow : IPaymentWorkflowService
    {
        public Task<Result> ApplyInvoicePaidStagedAsync(RentalOrder order, KafkaMessageEnvelope<InvoicePaidPayload> envelope, RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ProcessInvoicePaidAsync(KafkaMessageEnvelope<InvoicePaidPayload> envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class StubRefundConsumer : IRefundCompletedConsumer
    {
        public Task<Result> ApplyRefundCompletedStagedAsync(RentalOrder order, KafkaMessageEnvelope<RefundCompletedPayload> envelope, RentalInboxMessage inboxMessage, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ProcessRefundCompletedAsync(KafkaMessageEnvelope<RefundCompletedPayload> envelope, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
    private sealed class FakeOrderLockManager : IOrderLockManager
    {
        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        public Task<IAsyncDisposable> AcquireRenterLockAsync(Guid renterUserId, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable>(new NoOpDisposable());

        private sealed class NoOpDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
