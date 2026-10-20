using System.Globalization;
using System.Net.Sockets;
using Moq;
using System.Text.Json;
using Confluent.Kafka;
using Dprental.RecommendationService.Application.DTOs;
using Dprental.RecommendationService.Application.Services;
using Dprental.RecommendationService.Application.Validation;
using Dprental.RecommendationService.Domain.IRepositories;
using Dprental.RecommendationService.Domain.Repositories;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Cart;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Payment;
using Dprental.RentalService.Application.DTOs.Returns;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Events;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Application.Services.Cart;
using Dprental.RentalService.Application.Services.Integration;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.Services.Payments;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class RentalToRecommendationE2EIntegrationTests
{
    private const string DefaultKafkaBootstrap = "127.0.0.1:29092";

    private static string GetKafkaBootstrap() =>
        Environment.GetEnvironmentVariable("RENTAL_TEST_KAFKA_BOOTSTRAP_SERVERS") ?? DefaultKafkaBootstrap;

    private static async Task EnsureInfrastructureAvailableAsync(string kafkaBootstrap, string pgConn)
    {
        bool pgOk = await PostgresTestDatabaseHelper.IsPostgreSqlAvailableAsync(pgConn);
        if (!pgOk)
        {
            Assert.Fail($"PostgreSQL is not available at {pgConn}. Acceptance tests require real infrastructure.");
        }

        bool kafkaOk = await CanConnectToBrokerAsync(kafkaBootstrap);
        if (!kafkaOk)
        {
            Assert.Fail($"Kafka broker is not available at {kafkaBootstrap}. Acceptance tests require real infrastructure.");
        }
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
    public async Task FullChain_Cart_Paid_Completed_Accumulates23Points_AndIdempotentReplayDoesNotDuplicate()
    {
        string kafkaBootstrap = GetKafkaBootstrap();
        string basePgConn = PostgresTestDatabaseHelper.BaseConnectionString;
        await EnsureInfrastructureAvailableAsync(kafkaBootstrap, basePgConn);

        // 1. Separate dynamic test databases for Rental and Recommendation
        var (rentalConn, rentalDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync(basePgConn);
        var (recConn, recDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync(basePgConn);

        string testTopic = $"test.rental.events.{Guid.NewGuid():N}";

        try
        {
            // Migrate both databases
            var rentalDbOptions = PostgresTestDatabaseHelper.CreateOptions(rentalConn);
            await using (var rDb = new RentalServiceDbContext(rentalDbOptions))
            {
                await rDb.Database.MigrateAsync();
            }

            var recDbOptions = new DbContextOptionsBuilder<RecommendationServiceDbContext>()
                .UseNpgsql(recConn)
                .Options;
            await using (var recDb = new RecommendationServiceDbContext(recDbOptions))
            {
                await recDb.Database.MigrateAsync();
            }

            // Setup Rental services
            var timeProvider = TimeProvider.System;
            var kafkaOptions = new KafkaOptions
            {
                Enabled = true,
                BootstrapServers = kafkaBootstrap,
                RentalEventsTopic = testTopic,
                OutboxPollingIntervalSeconds = 1,
                DeliveryTimeoutSeconds = 5
            };

            await using var rentalDb = new RentalServiceDbContext(rentalDbOptions);
            var cartRepo = new RentalCartRepository(rentalDb);
            var outboxRepo = new OutboxRepository(rentalDb);
            var outboxWriter = new KafkaOutboxWriter(outboxRepo);
            var eventProducer = new RentalEventProducer(outboxWriter, Options.Create(kafkaOptions), timeProvider);
            var lockManager = new FakeOrderLockManager();

            var renterId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var listingId = Guid.NewGuid();
            var typeId = Guid.NewGuid();

            var fakeListingReader = new StubListingReader(new RentalListingSnapshot(
                listingId, supplierId, typeId, true, true, 500_000m, 1_000_000m, "v1"));

            var cartUseCases = new RentalCartUseCases(
                cartRepo,
                fakeListingReader,
                lockManager,
                rentalDb,
                eventProducer,
                timeProvider);

            // ==========================================
            // STEP 1: Add to Cart (CartItemAdded -> +5)
            // ==========================================
            var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
            var endDate = startDate.AddDays(2);

            var cartResult = await cartUseCases.AddCartItemAsync(renterId, listingId, 1, startDate, endDate);
            Assert.True(cartResult.IsSuccess, cartResult.Error.Message);
            var cartItemId = cartResult.Value.Id;

            // Verify Outbox row exists and is committed
            var outboxMessages = await rentalDb.RentalOutboxMessages.AsNoTracking().ToListAsync();
            var cartOutbox = Assert.Single(outboxMessages, m => m.MessageType == KafkaContractSpec.EventTypes.CartItemAdded);
            Assert.Equal(testTopic, cartOutbox.Topic);
            Assert.Contains(typeId.ToString("D"), cartOutbox.Payload);
            Assert.Contains("\"occurredAt\"", cartOutbox.Payload);
            Assert.Contains("Z\"", cartOutbox.Payload); // Confirms UTC Z suffix!

            // Publish via OutboxPublisherWorker
            using var transport = new KafkaProducerTransport(Options.Create(kafkaOptions), NullLogger<KafkaProducerTransport>.Instance);
            var services = new ServiceCollection();
            services.AddScoped<IOutboxRepository>(_ => new OutboxRepository(new RentalServiceDbContext(rentalDbOptions)));
            var sp = services.BuildServiceProvider();
            var publisherWorker = new OutboxPublisherWorker(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(kafkaOptions), NullLogger<OutboxPublisherWorker>.Instance, transport);

            int published1 = await publisherWorker.ProcessBatchAsync(CancellationToken.None);
            Assert.True(published1 >= 1);

            // Consume message from Kafka
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = kafkaBootstrap,
                GroupId = $"e2e-rec-group-{Guid.NewGuid():N}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            };

            using var consumer = new ConsumerBuilder<string?, string>(consumerConfig).Build();
            consumer.Subscribe(testTopic);

            var cr1 = consumer.Consume(TimeSpan.FromSeconds(8));
            Assert.NotNull(cr1);
            Assert.Equal(KafkaContractSpec.EventTypes.CartItemAdded, JsonDocument.Parse(cr1.Message.Value).RootElement.GetProperty("messageType").GetString());

            // Ingest into RecommendationService
            await using var recDb1 = new RecommendationServiceDbContext(recDbOptions);
            var recEventRepo = new InteractionEventRepository(recDb1);
            var recUserInterestRepo = new UserEquipmentTypeInterestRepository(recDb1);
            var recPopularityRepo = new ListingPopularityRepository(recDb1);
            var recUow = new InteractionUnitOfWork(recDb1);
            var recValidator = new EventSchemaValidator();

            var ingestionService = new InteractionIngestionService(
                recValidator,
                recEventRepo,
                recUserInterestRepo,
                recPopularityRepo,
                recUow,
                NullLogger<InteractionIngestionService>.Instance);

            var ingestRes1 = await ingestionService.ProcessEventAsync(cr1.Message.Value, InteractionEventSource.Rental, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.Processed, ingestRes1.Outcome);

            // Verify +5 points
            var interest1 = await recUserInterestRepo.GetTopInterestsAsync(renterId, 10, CancellationToken.None);
            var userInterest1 = Assert.Single(interest1);
            Assert.Equal(typeId, userInterest1.EquipmentTypeId);
            Assert.Equal(5, userInterest1.Score);

            var pop1 = await recPopularityRepo.GetTopPopularListingsAsync(10, CancellationToken.None);
            var listingPop1 = Assert.Single(pop1);
            Assert.Equal(listingId, listingPop1.EquipmentListingId);
            Assert.Equal(5, listingPop1.Score);

            // ==========================================
            // STEP 2: Invoice Paid (RentalItemPaid -> +8)
            // ==========================================
            var orderId = Guid.NewGuid();
            var rentalItemId = Guid.NewGuid();
            var assignments = Enumerable.Range(0, 1)
                .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
                .ToList();

            var itemDto = new InitialRentalItemDto(
                rentalItemId,
                listingId,
                supplierId,
                1,
                new DateOnly(2026, 10, 10),
                new DateOnly(2026, 10, 15),
                "Camera Equipment",
                "Professional Camera Kit",
                "District 1, HCMC",
                200_000m,
                0m,
                $"group-{rentalItemId:N}",
                assignments,
                new RentalItemListingSnapshotDto(typeId, null, null, null, null, null, null, Guid.NewGuid()));

            var order = RentalOrder.Create(
                orderId,
                $"ORD-{orderId.ToString("N")[..8]}",
                renterId,
                "VND",
                "Pickup",
                "Customer A",
                "0909123456",
                "123 Nguyen Hue",
                "Call before delivery",
                "bank-ref-001",
                "v1.0",
                "terms-doc-001",
                "sha256-hash-001",
                $"op-create-{orderId:N}",
                DateTimeOffset.UtcNow,
                [itemDto]).Value;

            order.AttachInvoice("INV-TEST-001", DateTimeOffset.UtcNow);
            var item = order.Items.First();

            await using (var rDb2 = new RentalServiceDbContext(rentalDbOptions))
            {
                rDb2.RentalOrders.Add(order);
                await rDb2.SaveChangesAsync();
            }

            var invoicePaidPayload = new InvoicePaidPayload(
                orderId, renterId, "INV-TEST-001", "PAY-TEST-001",
                order.TotalPayableAmount, order.Currency, DateTimeOffset.UtcNow);

            var invoiceEnvelope = new KafkaMessageEnvelope<InvoicePaidPayload>(
                Guid.NewGuid(), "InvoicePaid", 1, "payment-service",
                orderId.ToString("D"), DateTimeOffset.UtcNow, "corr-001", null, invoicePaidPayload);

            var inboxMsg = new RentalInboxMessage(
                Guid.NewGuid(), invoiceEnvelope.MessageId, "InvoicePaid",
                "payment-service", orderId.ToString("D"), "corr-001", null,
                JsonSerializer.Serialize(invoiceEnvelope), DateTimeOffset.UtcNow, "fp-001");

            var workflowConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:RentalEventsTopic"] = testTopic
            }).Build();

            await using var rDbWorkflow = new RentalServiceDbContext(rentalDbOptions);
            var workflowOrderRepo = new RentalOrderRepository(rDbWorkflow);
            var workflowInboxRepo = new InboxRepository(rDbWorkflow);
            var workflowOutboxRepo = new OutboxRepository(rDbWorkflow);
            var workflowReceiptRepo = new OperationReceiptRepository(rDbWorkflow);
            var workflowEventProducer = new RentalEventProducer(new KafkaOutboxWriter(workflowOutboxRepo), Options.Create(kafkaOptions), timeProvider);

            var paymentWorkflow = new PaymentWorkflowService(
                workflowOrderRepo,
                workflowInboxRepo,
                workflowOutboxRepo,
                workflowReceiptRepo,
                lockManager,
                new Mock<IRentalOperationExecutor>().Object,
                rDbWorkflow,
                null!,
                workflowConfig,
                NullLogger<PaymentWorkflowService>.Instance,
                timeProvider,
                workflowEventProducer);
            var paidStagedResult = await paymentWorkflow.ApplyInvoicePaidStagedAsync(order, invoiceEnvelope, inboxMsg, CancellationToken.None);
            Assert.True(paidStagedResult.IsSuccess, paidStagedResult.Error.Message);
            await rDbWorkflow.SaveChangesAsync();

            // Publish Outbox
            int published2 = await publisherWorker.ProcessBatchAsync(CancellationToken.None);
            Assert.True(published2 >= 1);

            var cr2 = consumer.Consume(TimeSpan.FromSeconds(8));
            Assert.NotNull(cr2);
            Assert.Equal(KafkaContractSpec.EventTypes.RentalItemPaid, JsonDocument.Parse(cr2.Message.Value).RootElement.GetProperty("messageType").GetString());

            var ingestRes2 = await ingestionService.ProcessEventAsync(cr2.Message.Value, InteractionEventSource.Rental, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.Processed, ingestRes2.Outcome);

            // Verify 5 + 8 = 13 points
            var interest2 = await recUserInterestRepo.GetTopInterestsAsync(renterId, 10, CancellationToken.None);
            Assert.Equal(13, interest2.First(x => x.EquipmentTypeId == typeId).Score);

            var pop2 = await recPopularityRepo.GetTopPopularListingsAsync(10, CancellationToken.None);
            Assert.Equal(13, pop2.First(x => x.EquipmentListingId == listingId).Score);

            // ==========================================
            // STEP 3: Settlement Completed (RentalItemCompleted -> +10)
            // ==========================================
            var now = DateTimeOffset.UtcNow;
            item.ApplySupplierAccept(supplierId, now, "CONF-001", "op-conf");
            item.ApplyHandover(now, "op-handover");
            var handoverRecord = new HandoverRecord(Guid.NewGuid(), item.Id, now.AddHours(-1), null, supplierId, now.AddHours(-1), now.AddHours(-1));
            item.SetHandoverRecord(handoverRecord);
            foreach (var unit in item.UnitAssignments)
            {
                unit.RecordHandover(handoverRecord.Id, UnitCondition.Normal, null, []);
            }
            string opKey = "settle-op-001";
            var settleReq = new SettleRentalRequest(
                orderId, rentalItemId, "INV-TEST-001", "PAY-TEST-001",
                supplierId, 0m, 0m, 0m, "VND", "DEC-001", opKey);

            var receipt = RentalOperationReceipt.CreatePending(
                Guid.NewGuid(), orderId, opKey, RentalItem.SettleRentalAction,
                "fp-settle", now, rentalItemId, JsonSerializer.Serialize(settleReq));
            order.AddOperationReceipt(receipt);

            // Normal return transitions item to PendingSettlement with FinalDeductionAmount = 0
            var returnUnits = item.UnitAssignments.Select(u => new ReturnUnitData(u.Id, UnitCondition.Normal, "OK", [])).ToList();
            var returnResult = item.RecordAssetReturns(supplierId, now, "Normal return", returnUnits, now, supplierId);
            Assert.True(returnResult.IsSuccess, returnResult.Error?.Message);
            var closureRes = item.ConfirmReturnedUnitClosure(item.UnitAssignments.First().Id, now, null, "CLOSURE-REF-1", now);
            Assert.True(closureRes.IsSuccess, closureRes.Error?.Message);

            var settledPayload = new RentalSettledPayload(
                orderId, rentalItemId, "INV-TEST-001", "PAY-TEST-001",
                "SETTLE-REF-001", opKey, 0m, 0m, "VND", null, now);

            var settledEnvelope = new KafkaMessageEnvelope<RentalSettledPayload>(
                Guid.NewGuid(), "RentalSettled", 1, "payment-service",
                orderId.ToString("D"), now, "corr-002", null, settledPayload);

            var settledConsumer = new RentalSettledConsumer(workflowEventProducer, timeProvider);
            var settledResult = await settledConsumer.ApplyRentalSettledStagedAsync(order, settledEnvelope, inboxMsg, CancellationToken.None);
            Assert.True(settledResult.IsSuccess, settledResult.Error.Message);
            await rDbWorkflow.SaveChangesAsync();

            // Publish Outbox
            int published3 = await publisherWorker.ProcessBatchAsync(CancellationToken.None);
            Assert.True(published3 >= 1);

            var cr3 = consumer.Consume(TimeSpan.FromSeconds(8));
            Assert.NotNull(cr3);
            Assert.Equal(KafkaContractSpec.EventTypes.RentalItemCompleted, JsonDocument.Parse(cr3.Message.Value).RootElement.GetProperty("messageType").GetString());

            var ingestRes3 = await ingestionService.ProcessEventAsync(cr3.Message.Value, InteractionEventSource.Rental, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.Processed, ingestRes3.Outcome);

            // ==========================================
            // FINAL ASSERTION: Exactly 5 + 8 + 10 = 23!
            // ==========================================
            var interestFinal = await recUserInterestRepo.GetTopInterestsAsync(renterId, 10, CancellationToken.None);
            Assert.Equal(23, interestFinal.First(x => x.EquipmentTypeId == typeId).Score);

            var popFinal = await recPopularityRepo.GetTopPopularListingsAsync(10, CancellationToken.None);
            Assert.Equal(23, popFinal.First(x => x.EquipmentListingId == listingId).Score);

            // ==========================================
            // IDEMPOTENT REPLAY & DEDUPLICATION TEST:
            // Re-ingesting all three messages must produce DuplicateIgnored and NOT increase score!
            // ==========================================
            var replay1 = await ingestionService.ProcessEventAsync(cr1.Message.Value, InteractionEventSource.Rental, CancellationToken.None);
            var replay2 = await ingestionService.ProcessEventAsync(cr2.Message.Value, InteractionEventSource.Rental, CancellationToken.None);
            var replay3 = await ingestionService.ProcessEventAsync(cr3.Message.Value, InteractionEventSource.Rental, CancellationToken.None);

            Assert.Equal(EventProcessOutcome.DuplicateIgnored, replay1.Outcome);
            Assert.Equal(EventProcessOutcome.DuplicateIgnored, replay2.Outcome);
            Assert.Equal(EventProcessOutcome.DuplicateIgnored, replay3.Outcome);

            var interestAfterReplay = await recUserInterestRepo.GetTopInterestsAsync(renterId, 10, CancellationToken.None);
            Assert.Equal(23, interestAfterReplay.First(x => x.EquipmentTypeId == typeId).Score);

            var popAfterReplay = await recPopularityRepo.GetTopPopularListingsAsync(10, CancellationToken.None);
            Assert.Equal(23, popAfterReplay.First(x => x.EquipmentListingId == listingId).Score);

            // ==========================================
            // CONFLICT MISMATCH TEST:
            // Same MessageId with different payload returns ConflictMismatch and score does not change!
            // ==========================================
            var conflictJson = cr1.Message.Value.Replace(renterId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
            var conflictRes = await ingestionService.ProcessEventAsync(conflictJson, InteractionEventSource.Rental, CancellationToken.None);
            Assert.Equal(EventProcessOutcome.ConflictMismatch, conflictRes.Outcome);

            var interestAfterConflict = await recUserInterestRepo.GetTopInterestsAsync(renterId, 10, CancellationToken.None);
            Assert.Equal(23, interestAfterConflict.First(x => x.EquipmentTypeId == typeId).Score);
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(rentalDbName, basePgConn);
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(recDbName, basePgConn);
        }
    }

    [Fact]
    public async Task OutboxPublisher_TransientBrokerOutage_RetriesPastThreshold_AndRecoversWhenBrokerAvailable()
    {
        string kafkaBootstrap = GetKafkaBootstrap();
        string basePgConn = PostgresTestDatabaseHelper.BaseConnectionString;
        await EnsureInfrastructureAvailableAsync(kafkaBootstrap, basePgConn);

        var (rentalConn, rentalDbName) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync(basePgConn);
        string testTopic = $"test.rental.outage.{Guid.NewGuid():N}";

        try
        {
            var rentalDbOptions = PostgresTestDatabaseHelper.CreateOptions(rentalConn);
            await using (var rDb = new RentalServiceDbContext(rentalDbOptions))
            {
                await rDb.Database.MigrateAsync();
            }

            var timeProvider = TimeProvider.System;
            var outboxMsg = new RentalOutboxMessage(
                Guid.NewGuid(),
                testTopic,
                "part-1",
                KafkaContractSpec.EventTypes.CartItemAdded,
                "{\"test\": \"payload\"}",
                DateTimeOffset.UtcNow);

            await using (var rDb = new RentalServiceDbContext(rentalDbOptions))
            {
                var repo = new OutboxRepository(rDb);
                await repo.AddAsync(outboxMsg);
                await rDb.SaveChangesAsync();
            }

            // Simulate 12 consecutive transient broker errors (past 10 threshold)
            await using (var rDb = new RentalServiceDbContext(rentalDbOptions))
            {
                var repo = new OutboxRepository(rDb);
                string claimToken = "claim-token-1";
                for (int i = 0; i < 12; i++)
                {
                    var msg = await repo.ClaimNextPendingAsync(claimToken, TimeSpan.FromMinutes(1));
                    Assert.NotNull(msg);

                    // Record failure as transient error (targetStatus = "Pending")
                    await repo.RecordFailureAsync(
                        msg.Id,
                        claimToken,
                        "Broker transport timeout / network unavailable",
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow.AddMilliseconds(-10),
                        "Pending");
                }

                // Verify row is STILL Pending (not DeadLettered!) and retry count is 12
                var finalMsg = await rDb.RentalOutboxMessages.AsNoTracking().FirstAsync(m => m.Id == outboxMsg.Id);
                Assert.Equal("Pending", finalMsg.Status);
                Assert.Equal(12, finalMsg.RetryCount);
            }

            // Broker recovers -> Publish to real Kafka
            var kafkaOptions = new KafkaOptions
            {
                Enabled = true,
                BootstrapServers = kafkaBootstrap,
                RentalEventsTopic = testTopic,
                DeliveryTimeoutSeconds = 5
            };

            using var transport = new KafkaProducerTransport(Options.Create(kafkaOptions), NullLogger<KafkaProducerTransport>.Instance);
            var services = new ServiceCollection();
            services.AddScoped<IOutboxRepository>(_ => new OutboxRepository(new RentalServiceDbContext(rentalDbOptions)));
            var sp = services.BuildServiceProvider();
            var publisherWorker = new OutboxPublisherWorker(sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(kafkaOptions), NullLogger<OutboxPublisherWorker>.Instance, transport);

            int published = await publisherWorker.ProcessBatchAsync(CancellationToken.None);
            Assert.Equal(1, published);

            await using (var rDb = new RentalServiceDbContext(rentalDbOptions))
            {
                var publishedMsg = await rDb.RentalOutboxMessages.AsNoTracking().FirstAsync(m => m.Id == outboxMsg.Id);
                Assert.Equal("Published", publishedMsg.Status);
                Assert.NotNull(publishedMsg.PublishedAt);
            }
        }
        finally
        {
            await PostgresTestDatabaseHelper.DropTestDatabaseAsync(rentalDbName, basePgConn);
        }
    }

    private sealed class StubListingReader(RentalListingSnapshot snapshot) : IRentalListingReader
    {
        public Task<Result<RentalListingSnapshot>> GetListingAsync(Guid listingId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(snapshot));
    }
}
