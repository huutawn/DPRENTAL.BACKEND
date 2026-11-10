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
        item.SetDamageLevelsSnapshot(DamageLevelTestData.SnapshotJson);
        await using var db = NewDb();
        db.Add(order);
        await db.SaveChangesAsync();
        return item;
    }

    private static IReadOnlyList<DamageAssessment> Assess(RentalItem item, int level) =>
        [new DamageAssessment(item.UnitAssignments.Single().AssetId, level)];

    private DeductionService Service(RentalServiceDbContext db, IRentalMediaClient media) => new(
        new RentalOrderRepository(db),
        new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
        db, media, _clock);

    private DeductionResponseService ResponseService(RentalServiceDbContext db) => new(
        new RentalOrderRepository(db),
        new PostgresOrderLockManager(Configuration(), NullLogger<PostgresOrderLockManager>.Instance),
        db, Mock.Of<IRentalMediaClient>(), _clock, Configuration(),
        NullLogger<DeductionResponseService>.Instance);

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
                    new SubmitDeductionRequest(Assess(item, 2), "Repair", [evidenceId]), "proposal-key");
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

    [Fact]
    public async Task DisputeHoldTimeout_PostgreSql_FinalizesProposedAmountOnce()
    {
        var item = await SeedDisputedAsync();
        _clock.SetUtcNow(item.ReturnsCompletedAt!.Value.AddDays(7));
        await using var db = NewDb();
        var service = ResponseService(db);
        Assert.Equal(1, await service.ProcessDisputeHoldTimeoutsAsync());
        Assert.Equal(0, await service.ProcessDisputeHoldTimeoutsAsync());
        var saved = (await new RentalOrderRepository(db).GetByItemIdWithDetailsAsync(item.Id))!
            .Items.Single(i => i.Id == item.Id);
        decimal proposedAmount = (await db.DeductionProposals.SingleAsync()).TotalProposedAmount;
        Assert.Equal(proposedAmount, saved.FinalDeductionAmount);
        Assert.Equal(saved.DepositAmount - proposedAmount, saved.DepositRefundAmount);
        Assert.Equal(RentalItemStatus.PendingSettlement, saved.Status);
    }
    [Fact]
    public async Task ProposalPersistsAndReplaysWithoutExtendingDeadline()
    {
        var item = await SeedDamagedAsync();
        Guid mediaId = Guid.NewGuid();
        var request = new SubmitDeductionRequest(Assess(item, 2), "Repair", [mediaId]);
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
                request with { Assessments = Assess(item, 3) }, "deduction-key");
            Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error.Code);
            Assert.Equal(0, await Service(db, media.Object).ProcessDeductionTimeoutsAsync());
        }
        await using var check = NewDb();
        Assert.Single(await check.DeductionProposals.ToListAsync());
        Assert.Equal(150_000m, (await check.DeductionProposals.SingleAsync()).TotalProposedAmount);
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
        var request = new SubmitDeductionRequest(Assess(item, 2), "Repair", [mediaId]);

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
        var request = new SubmitDeductionRequest(Assess(item, 2), "Damage", [Guid.NewGuid()]);
        string route = $"/api/v1/rental/items/{item.Id}/deductions";
        using var anonymous = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "dev-internal-api-key-dprental-2026");
        client.DefaultRequestHeaders.Add("X-Test-Renter-Id", Guid.NewGuid().ToString());
        using var foreign = await client.PostAsJsonAsync(route, request);
        // Supplier khÃ¡c nháº­n 404 nhÆ° khi khoáº£n thuÃª khÃ´ng tá»“n táº¡i (IDOR).
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }
}
