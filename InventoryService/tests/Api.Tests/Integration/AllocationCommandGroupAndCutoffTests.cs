using System.Net.Http.Headers;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Media;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Kafka.Transport;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class AllocationCommandGroupAndCutoffTests : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresFixture _fixture;

    public AllocationCommandGroupAndCutoffTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class StubCatalogClient : ICatalogServiceClient
    {
        public Task<Result<CatalogEquipmentTypeResponse>> GetPublishedEquipmentTypeAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(
                equipmentTypeId, "EQ-FX3", "Sony FX3 Cinema Line", null, 1, DateTime.UtcNow, null, null)));

        public Task<Result<CatalogBrandResponse>> GetBrandAsync(Guid brandId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new CatalogBrandResponse(
                brandId, "BR-SONY", "Sony", true)));

        public Task<Result> ValidateSpecificationAsync(Guid equipmentTypeId, int schemaVersion, string specValues, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    private sealed class StubMediaClient : IMediaServiceClient
    {
        public Task<Result<MediaVerificationInfo>> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVerificationInfo(mediaId, "Ready", Guid.NewGuid().ToString("D"), "image/jpeg")));

        public Task<Result<MediaVisibilityInfo>> SetPublicAsync(Guid mediaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(new MediaVisibilityInfo(mediaId, "Public")));
    }

    private (WebApplicationFactory<Program> App, InventorySecurityTestFactory SecFactory) CreateFactory(TestTimeProvider? clock = null)
    {
        var testClock = clock ?? new TestTimeProvider(FixedNow);
        var secFactory = new InventorySecurityTestFactory(_fixture.ConnectionString, testClock);
        var app = secFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICatalogServiceClient>();
                services.AddSingleton<ICatalogServiceClient>(new StubCatalogClient());

                services.RemoveAll<IMediaServiceClient>();
                services.AddSingleton<IMediaServiceClient>(new StubMediaClient());
            });
        });
        return (app, secFactory);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, o => o.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<EquipmentListing> CreateListingWithUnitsAsync(Guid ownerId, int unitCount, string serialPrefix = "FX3-GRP")
    {
        await using var db = CreateDbContext();
        var listing = EquipmentListing.Create(
            Guid.NewGuid(),
            ownerId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3 Cinema Line",
            null,
            "Sony FX3 Cinema Line",
            1,
            "{}",
            100_000m,
            null,
            null,
            "VND",
            FixedNow,
            2024).Value;

        if (!await db.Warehouses.AnyAsync(w => w.OwnerUserId == ownerId))
        {
            db.Warehouses.Add(Warehouse.CreateWithAddress(ownerId, "123 Test Street", "01", "Thành phố Hà Nội", "00070", "Phường Hoàn Kiếm", 21.0, 105.8, FixedNow).Value);
        }
        listing.UpdateStatus(EquipmentListingStatus.Active, true, FixedNow);
        db.EquipmentListings.Add(listing);

        for (int i = 1; i <= unitCount; i++)
        {
            var unit = InventoryAsset.Create(
                Guid.NewGuid(),
                listing.Id,
                $"{serialPrefix}-{i:D4}",
                FixedNow).Value;
            db.Assets.Add(unit);
        }

        await db.SaveChangesAsync();
        return listing;
    }

    [Fact]
    public async Task ProcessCreateHoldCommand_WithCustomGroupReference_CreatesAllocationsUnderCustomGroup()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IInventoryAllocationService>();

        var listing = await CreateListingWithUnitsAsync(Guid.NewGuid(), 2, "GRP-TEST");
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        string customGroup = $"rental-item:{itemId}:rec-1";

        var payload = new CreateHoldPayload(
            RentalOrderId: orderId,
            RentalItemId: itemId,
            OperationKey: "op-create-hold-custom-grp",
            EquipmentListingId: listing.Id,
            Quantity: 1,
            StartDate: new DateOnly(2026, 11, 1),
            EndDate: new DateOnly(2026, 11, 5),
            HoldExpiresAt: FixedNow.AddHours(2),
            Notes: "Hold with custom group",
            AllocationGroupId: customGroup);

        var envelope = MessageEnvelope.CreateRoot(
            AllocationMessageTypes.CreateHold,
            orderId.ToString("D"),
            payload,
            orderId.ToString("D"),
            producer: "rental-service",
            occurredAt: FixedNow);

        var outcome = await service.ProcessCreateHoldCommandAsync(envelope, KafkaServiceCallerContext.Instance, "TestHandler");
        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.Value.IsSuccess);
        Assert.Equal(customGroup, outcome.Value.SuccessPayload?.AllocationGroupId);

        // Verify in DB that the allocation was tagged with customGroup
        await using var db = CreateDbContext();
        var allocs = await db.AssetAllocations.Where(a => a.GroupReference == customGroup).ToListAsync();
        Assert.Single(allocs);
        Assert.Equal(customGroup, allocs[0].GroupReference);
    }

    [Fact]
    public async Task ProcessCreateHoldCommand_WithExpiredCutoff_RejectsWithCutoffExpired()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IInventoryAllocationService>();

        var listing = await CreateListingWithUnitsAsync(Guid.NewGuid(), 2, "CUT-TEST");
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        var payload = new CreateHoldPayload(
            RentalOrderId: orderId,
            RentalItemId: itemId,
            OperationKey: "op-create-hold-expired-cutoff",
            EquipmentListingId: listing.Id,
            Quantity: 1,
            StartDate: new DateOnly(2026, 11, 1),
            EndDate: new DateOnly(2026, 11, 5),
            HoldExpiresAt: FixedNow.AddHours(2),
            Notes: "Hold with expired cutoff",
            AllocationGroupId: $"rental-item:{itemId}:rec-cutoff",
            ExecutionCutoffUtc: FixedNow.AddMinutes(-1)); // Cutoff in the past!

        var envelope = MessageEnvelope.CreateRoot(
            AllocationMessageTypes.CreateHold,
            orderId.ToString("D"),
            payload,
            orderId.ToString("D"),
            producer: "rental-service",
            occurredAt: FixedNow);

        var outcome = await service.ProcessCreateHoldCommandAsync(envelope, KafkaServiceCallerContext.Instance, "TestHandler");
        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.Value.IsSuccess);
        Assert.Equal("CUTOFF_EXPIRED", outcome.Value.RejectionPayload?.ErrorCode);
    }

    [Fact]
    public async Task ProcessReleaseAllocationCommand_WithCustomGroup_OnlyReleasesCustomGroup()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;

        using var scope = app.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IInventoryAllocationService>();

        var listing = await CreateListingWithUnitsAsync(Guid.NewGuid(), 3, "ISO-GRP");
        var orderId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        string initialGroup = $"rental-item:{itemId}";
        string recoveryGroup = $"rental-item:{itemId}:rec-1";

        // 1. Create initial hold under initialGroup
        var payloadInit = new CreateHoldPayload(
            orderId, itemId, "op-init-hold", listing.Id, 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), FixedNow.AddHours(2),
            AllocationGroupId: initialGroup);
        var envInit = MessageEnvelope.CreateRoot(
            AllocationMessageTypes.CreateHold, orderId.ToString("D"), payloadInit, orderId.ToString("D"),
            producer: "rental-service", occurredAt: FixedNow);
        var resInit = await service.ProcessCreateHoldCommandAsync(envInit, KafkaServiceCallerContext.Instance, "TestHandler");
        Assert.True(resInit.Value.IsSuccess);

        // 2. Create recovery hold under recoveryGroup
        var payloadRec = new CreateHoldPayload(
            orderId, itemId, "op-rec-hold", listing.Id, 1,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5), FixedNow.AddHours(2),
            AllocationGroupId: recoveryGroup);
        var envRec = MessageEnvelope.CreateRoot(
            AllocationMessageTypes.CreateHold, orderId.ToString("D"), payloadRec, orderId.ToString("D"),
            producer: "rental-service", occurredAt: FixedNow);
        var resRec = await service.ProcessCreateHoldCommandAsync(envRec, KafkaServiceCallerContext.Instance, "TestHandler");
        Assert.True(resRec.Value.IsSuccess);

        // 3. Release initialGroup
        var payloadRelInit = new ReleaseAllocationPayload(orderId, itemId, "op-rel-init", "Expired initial hold", initialGroup);
        var envRelInit = MessageEnvelope.CreateRoot(
            AllocationMessageTypes.ReleaseAllocation, orderId.ToString("D"), payloadRelInit, orderId.ToString("D"),
            producer: "rental-service", occurredAt: FixedNow);
        var resRelInit = await service.ProcessReleaseAllocationCommandAsync(envRelInit, KafkaServiceCallerContext.Instance, "TestHandler");
        Assert.True(resRelInit.Value.IsSuccess);

        // 4. Verify in DB: initialGroup is Released, recoveryGroup is still Held!
        await using var db = CreateDbContext();
        var allocInit = await db.AssetAllocations.SingleAsync(a => a.GroupReference == initialGroup);
        var allocRec = await db.AssetAllocations.SingleAsync(a => a.GroupReference == recoveryGroup);

        Assert.Equal(AllocationStatus.Released, allocInit.Status);
        Assert.Equal(AllocationStatus.Held, allocRec.Status);
    }
}
