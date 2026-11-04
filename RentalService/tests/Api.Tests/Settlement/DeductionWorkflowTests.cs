using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Confluent.Kafka;
using Dprental.RentalService.Api.Tests.Cart;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Media;
using Dprental.RentalService.Application.DTOs.Settlement;
using Dprental.RentalService.Application.Kafka.Configuration;
using Dprental.RentalService.Application.Kafka.Contracts;
using Dprental.RentalService.Application.Kafka.Producers;
using Dprental.RentalService.Application.Kafka.Workers;
using Dprental.RentalService.Application.Security;
using Dprental.RentalService.Application.Services.Settlement;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Settlement;

public sealed class DeductionWorkflowTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private string _connection = "";
    private string _database = "";

    public async Task InitializeAsync()
    {
        (_connection, _database) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        await using var db = NewDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(PostgresTestDatabaseHelper.BaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private RentalServiceDbContext NewDb() => new(PostgresTestDatabaseHelper.CreateOptions(_connection));
    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _connection }).Build();

    private static RentalCallerContext Caller(Guid id) => RentalCallerContext.FromClaimsPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id.ToString())], "test")));

    private async Task<RentalItem> SeedDamagedAsync()
    {
        var order = Returns.ReturnTests.NewOrder(1);
        var item = order.Items.Single();
        var unit = item.UnitAssignments.Single();
        ReturnTestDataSeeder.SeedReturn(item, item.SupplierId, Now.AddHours(-1), null,
            [new ReturnUnitData(unit.Id, UnitCondition.Damaged, "Damage", [])], Now, item.SupplierId);
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return item;
    }

    private DeductionService Service(RentalServiceDbContext db, IRentalMediaClient media) => new(
        new RentalOrderRepository(db),
        new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
        db, media, _clock);

    private DeductionResponseService ResponseService(RentalServiceDbContext db) => new(
        new RentalOrderRepository(db),
        new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
        db, Mock.Of<IRentalMediaClient>(), _clock, Configuration(),
        NullLogger<DeductionResponseService>.Instance,
        new RentalEventProducer(new KafkaOutboxWriter(new OutboxRepository(db)),
            Options.Create(new KafkaOptions())));

    private static Mock<IRentalMediaClient> ReadyMedia(Guid mediaId, Guid uploader)
    {
        var media = new Mock<IRentalMediaClient>();
        media.Setup(m => m.VerifyMediaAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaVerifyDto(mediaId, uploader.ToString(), "Ready")));
        media.Setup(m => m.GetMetadataAsync(mediaId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(new MediaMetadataDto(mediaId, "Private", "Ready")));
        return media;
    }

    private async Task<RentalItem> SeedDisputedAsync()
    {
        var item = await SeedDamagedAsync();
        var evidenceId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            var submitted = await Service(db, ReadyMedia(evidenceId, item.SupplierId).Object)
                .SubmitAsync(item.Id, Caller(item.SupplierId),
                    new SubmitDeductionRequest(100_000m, "Repair", [evidenceId],
                        [item.UnitAssignments.Single().AssetId]), "proposal-key");
            Assert.True(submitted.IsSuccess, submitted.Error.Message);
        }

        await using (var db = NewDb())
        {
            var disputed = await ResponseService(db).RespondAsync(item.Id,
                Caller(item.RentalOrder!.RenterId),
                new RespondDeductionRequest(RenterDeductionDecision.Dispute, "Disagree", null),
                "dispute-key");
            Assert.True(disputed.IsSuccess, disputed.Error.Message);
        }

        return item;
    }

    [Theory]
    [InlineData(0, "Rejected")]
    [InlineData(20_000, "Partial")]
    [InlineData(100_000, "UpheldInFull")]
    public async Task AdminResolution_PostgreSql_CommitsDecisionAndOutboxOnce(
        int finalAmount, string expectedDecision)
    {
        var item = await SeedDisputedAsync();

        var admin = RentalCallerContext.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("r", "adm")], "test")));
        var request = new ResolveDisputeRequest(finalAmount, "Admin reviewed damage evidence", null);
        await using (var db = NewDb())
        {
            var service = ResponseService(db);
            var resolved = await service.ResolveDisputeAsync(item.Id, admin, request, "resolution-key");
            Assert.True(resolved.IsSuccess, resolved.Error.Message);
            var replay = await service.ResolveDisputeAsync(item.Id, admin, request, "resolution-key");
            Assert.True(replay.IsSuccess, replay.Error.Message);
        }

        await using var check = NewDb();
        var savedItem = await check.RentalItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(finalAmount, savedItem.FinalDeductionAmount);
        Assert.Equal(1, await check.RentalOperationReceipts.AsNoTracking()
            .CountAsync(r => r.Action == "AdminResolveDispute"));
        var message = Assert.Single(await check.RentalOutboxMessages.AsNoTracking()
            .Where(m => m.MessageType == KafkaContractSpec.EventTypes.RentalDeductionDisputeResolved)
            .ToListAsync());
        Assert.Equal(KafkaContractSpec.Topics.RentalEvents, message.Topic);
        using var json = JsonDocument.Parse(message.Payload);
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(expectedDecision, payload.GetProperty("decision").GetString());
        Assert.Equal(finalAmount, payload.GetProperty("finalDeductionAmount").GetDecimal());
        var savedOrder = await check.RentalOrders.AsNoTracking().SingleAsync(o => o.Id == savedItem.RentalOrderId);
        Assert.Equal(savedOrder.Version, payload.GetProperty("orderVersion").GetInt64());
    }

    [Fact]
    public async Task AdminResolution_PostgreSql_SaveFailureRollsBackDecisionAndOutbox()
    {
        var item = await SeedDisputedAsync();

        var failingOptions = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseNpgsql(_connection)
            .AddInterceptors(new FailResolutionSaveInterceptor())
            .Options;
        await using (var failingDb = new RentalServiceDbContext(failingOptions))
        {
            var admin = RentalCallerContext.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", Guid.NewGuid().ToString()), new Claim("r", "adm")], "test")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ResponseService(failingDb).ResolveDisputeAsync(item.Id, admin,
                    new ResolveDisputeRequest(20_000m, "Partial damage confirmed", null),
                    "resolution-key"));
        }

        await using var check = NewDb();
        var saved = await check.RentalItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Null(saved.FinalDeductionAmount);
        Assert.Equal(DeductionProposalStatus.Disputed,
            (await check.DeductionProposals.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await check.RentalOperationReceipts.AsNoTracking()
            .CountAsync(r => r.Action == "AdminResolveDispute"));
        Assert.Equal(0, await check.RentalOutboxMessages.AsNoTracking()
            .CountAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalDeductionDisputeResolved));
    }

    private sealed class FailResolutionSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated failure after outbox enqueue");
    }

    [Fact]
    public async Task AdminResolution_PostgreSql_KafkaFailureRetriesCommittedEvent()
    {
        var item = await SeedDisputedAsync();
        var admin = RentalCallerContext.FromClaimsPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("r", "adm")], "test")));
        await using (var db = NewDb())
        {
            var resolved = await ResponseService(db).ResolveDisputeAsync(item.Id, admin,
                new ResolveDisputeRequest(20_000m, "Partial damage confirmed", null),
                "resolution-key");
            Assert.True(resolved.IsSuccess, resolved.Error.Message);
        }

        var transport = new Mock<IKafkaProducerTransport>();
        transport.SetupSequence(t => t.ProduceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("broker timeout"))
            .ReturnsAsync(new DeliveryResult<string, string>
            {
                Status = PersistenceStatus.Persisted
            });
        var services = new ServiceCollection();
        services.AddScoped<RentalServiceDbContext>(_ => NewDb());
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        using var provider = services.BuildServiceProvider();
        var publisher = new OutboxPublisherWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new KafkaOptions { Enabled = true, MaxBatchPerRound = 1 }),
            NullLogger<OutboxPublisherWorker>.Instance, transport.Object);

        Assert.Equal(0, await publisher.ProcessBatchAsync(CancellationToken.None));
        await using (var check = NewDb())
        {
            var pending = await check.RentalOutboxMessages.AsNoTracking().SingleAsync(
                m => m.MessageType == KafkaContractSpec.EventTypes.RentalDeductionDisputeResolved);
            Assert.Equal("Pending", pending.Status);
            Assert.Equal(1, pending.RetryCount);
            Assert.Equal(20_000m, (await check.RentalItems.AsNoTracking()
                .SingleAsync(i => i.Id == item.Id)).FinalDeductionAmount);
            await check.RentalOutboxMessages
                .Where(m => m.Id == pending.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ScheduledAt,
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        Assert.Equal(1, await publisher.ProcessBatchAsync(CancellationToken.None));
        await using var finalCheck = NewDb();
        Assert.Equal("Published", (await finalCheck.RentalOutboxMessages.AsNoTracking()
            .SingleAsync(m => m.MessageType == KafkaContractSpec.EventTypes.RentalDeductionDisputeResolved)).Status);
        transport.Verify(t => t.ProduceAsync(KafkaContractSpec.Topics.RentalEvents,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProposalPersistsAndReplaysWithoutExtendingDeadline()
    {
        var item = await SeedDamagedAsync();
        Guid mediaId = Guid.NewGuid();
        var request = new SubmitDeductionRequest(100_000m, "Repair", [mediaId], [item.UnitAssignments.Single().AssetId]);
        var media = ReadyMedia(mediaId, item.SupplierId);
        await using (var db = NewDb())
        {
            var result = await Service(db, media.Object).SubmitAsync(item.Id, Caller(item.SupplierId), request, "deduction-key");
            Assert.True(result.IsSuccess, result.Error.Message);
        }
        _clock.Advance(TimeSpan.FromDays(3));
        await using (var db = NewDb())
        {
            var replay = await Service(db, media.Object).SubmitAsync(item.Id, Caller(item.SupplierId), request, "deduction-key");
            Assert.True(replay.IsSuccess, replay.Error.Message);
            Assert.Equal(Now.AddHours(48), replay.Value.RenterResponseDeadline);
            var conflict = await Service(db, media.Object).SubmitAsync(item.Id, Caller(item.SupplierId),
                request with { DeductionAmount = 100_001m }, "deduction-key");
            Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error.Code);
            Assert.Equal(0, await Service(db, media.Object).ProcessDeductionTimeoutsAsync());
        }
        await using var check = NewDb();
        Assert.Single(await check.DeductionProposals.ToListAsync());
        Assert.Equal(100_000m, (await check.DeductionProposals.SingleAsync()).TotalProposedAmount);
        Assert.Null((await check.RentalUnitAssignments.SingleAsync()).ProposedDeductionAmount);
    }

    [Fact]
    public async Task TimeoutFinalizesZeroOnceAndKeepsItemPendingSettlement()
    {
        var item = await SeedDamagedAsync();
        _clock.Advance(TimeSpan.FromHours(48).Add(TimeSpan.FromMilliseconds(1)));
        await using (var db = NewDb())
        {
            var service = Service(db, Mock.Of<IRentalMediaClient>());
            Assert.Equal(1, await service.ProcessDeductionTimeoutsAsync());
            Assert.Equal(0, await service.ProcessDeductionTimeoutsAsync());
        }
        await using var check = NewDb();
        var saved = await check.RentalItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(0m, saved.FinalDeductionAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, saved.Status);
        Assert.Equal(1, await check.RentalOperationReceipts.CountAsync(r => r.Action == "DeductionTimeout"));
    }

    [Fact]
    public async Task ConcurrentProposalsForOneItemOnlyPersistOne()
    {
        var item = await SeedDamagedAsync();
        Guid mediaId = Guid.NewGuid();
        var media = ReadyMedia(mediaId, item.SupplierId);
        var request = new SubmitDeductionRequest(100_000m, "Repair", [mediaId], [item.UnitAssignments.Single().AssetId]);

        async Task<Result<SubmitDeductionResponse>> Send(string key)
        {
            await using var db = NewDb();
            return await Service(db, media.Object).SubmitAsync(item.Id, Caller(item.SupplierId), request, key);
        }

        var results = await Task.WhenAll(Send("proposal-a"), Send("proposal-b"));
        Assert.Single(results, result => result.IsSuccess);
        await using var check = NewDb();
        Assert.Equal(1, await check.DeductionProposals.CountAsync());
        Assert.Equal(1, await check.RentalOperationReceipts.CountAsync(r => r.Action == "SubmitDeduction"));
    }

    [Fact]
    public async Task ApiRejectsAnonymousAndForeignSupplier()
    {
        var item = await SeedDamagedAsync();
        using var parent = new RentalApiFixture();
        using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<RentalServiceDbContext>();
            services.AddScoped(_ => NewDb());
            services.RemoveAll<IOrderLockManager>();
            services.AddSingleton<IOrderLockManager>(new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance));
            services.AddSingleton<TimeProvider>(_clock);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "auth-check");
        var request = new SubmitDeductionRequest(1, "Damage", [Guid.NewGuid()], [item.UnitAssignments.Single().AssetId]);
        string route = $"/api/v1/rental/items/{item.Id}/deductions";
        using var anonymous = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", Guid.NewGuid().ToString());
        using var foreign = await client.PostAsJsonAsync(route, request);
        // Supplier khác nhận 404 như khi khoản thuê không tồn tại (IDOR).
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }
}
