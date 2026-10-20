using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Kiểm thử tích hợp HTTP API cho Hold &amp; Allocation Core với JWT/JWKS test infrastructure,
/// clock cố định và PostgreSQL 17 thật theo DPR-151.
/// </summary>
public sealed class AllocationHttpIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);

    public AllocationHttpIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private InventorySecurityTestFactory CreateFactory(TestTimeProvider? clock = null)
    {
        clock ??= new TestTimeProvider(FixedNow);
        return new InventorySecurityTestFactory(_fixture.ConnectionString, clock);
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId, bool acceptingBookings = true)
    {
        await using var db = CreateDbContext();
        return await IntegrationTestHelper.InsertTestAssetAsync(db, ownerId: ownerUserId, acceptingBookings: acceptingBookings, now: FixedNow);
    }

    [Fact]
    public async Task MissingOrInvalidToken_Returns401()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var assetId = Guid.NewGuid();
        var allocId = Guid.NewGuid();

        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = "REF-001",
            startDate = "2026-11-01",
            endDate = "2026-11-05",
            holdExpiresAt = "2026-10-10T10:00:00Z"
        };
        var holdContent = new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json");

        // 1. Không có token -> 401
        var holdNoToken = await client.PostAsync($"/api/v1/inventory/assets/{assetId}/holds", holdContent);
        Assert.Equal(HttpStatusCode.Unauthorized, holdNoToken.StatusCode);

        var allocContent = new StringContent(JsonSerializer.Serialize(new { operationKey = "OP-1" }), Encoding.UTF8, "application/json");
        var allocNoToken = await client.PostAsync($"/api/v1/inventory/allocations/{allocId}/allocate", allocContent);
        Assert.Equal(HttpStatusCode.Unauthorized, allocNoToken.StatusCode);

        // 2. Token sai / rác -> 401
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");
        var holdBadToken = await client.PostAsync($"/api/v1/inventory/assets/{assetId}/holds", holdContent);
        Assert.Equal(HttpStatusCode.Unauthorized, holdBadToken.StatusCode);
    }

    [Fact]
    public async Task HumanOrAdminOrServiceWithoutScope_IsForbidden_Returns403()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = "REF-002",
            startDate = FixedToday.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(15).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        };
        var holdContent = new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json");

        // A. Human bình thường (kể cả owner) -> 403
        var humanToken = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);
        var resHuman = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds", holdContent);
        Assert.Equal(HttpStatusCode.Forbidden, resHuman.StatusCode);

        // B. Admin human -> 403
        var adminToken = factory.CreateAdminToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var resAdmin = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds", holdContent);
        Assert.Equal(HttpStatusCode.Forbidden, resAdmin.StatusCode);

        // C. Service thiếu scope inventory.allocation.write -> 403
        var serviceNoScopeToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ["inventory.calendar.read"]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceNoScopeToken);
        var resService = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds", holdContent);
        Assert.Equal(HttpStatusCode.Forbidden, resService.StatusCode);
    }

    [Fact]
    public async Task CreateHold_Success201_ReturnsExpectedResponseStructure_AndStagedOutbox()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        string extRef = $"REF-SUCCESS-{Guid.NewGuid():N}";
        var holdPayload = new
        {
            sourceSystem = "rental-platform",
            externalReference = extRef,
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(5).ToString("O"),
            notes = "Customer reservation"
        };
        var content = new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.Id);
        Assert.Equal(asset.Id, body.AssetId);
        Assert.Equal("rental-platform", body.SourceSystem);
        Assert.Equal(extRef, body.ExternalReference);
        Assert.Equal(FixedToday.AddDays(5), body.StartDate);
        Assert.Equal(FixedToday.AddDays(8), body.EndDate);
        Assert.Equal("Held", body.Status);
        Assert.Equal("Customer reservation", body.Notes);
        Assert.Equal(FixedNow, body.CreatedAt);
        Assert.Equal(FixedNow, body.UpdatedAt);

        // Xác nhận JSON không expose xmin, transitions, owner
        var rawJson = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(rawJson);
        Assert.False(jsonDoc.RootElement.TryGetProperty("xmin", out _));
        Assert.False(jsonDoc.RootElement.TryGetProperty("version", out _));
        Assert.False(jsonDoc.RootElement.TryGetProperty("transitions", out _));
        Assert.False(jsonDoc.RootElement.TryGetProperty("ownerUserId", out _));
        Assert.False(jsonDoc.RootElement.TryGetProperty("asset", out _));

        // Kiểm tra Outbox trong DB
        await using var db = CreateDbContext();
        var outboxMsg = await db.OutboxMessages.SingleOrDefaultAsync(m => m.KafkaKey == asset.Id.ToString("D"));
        Assert.NotNull(outboxMsg);
        Assert.Equal("inventory.asset.events.v1", outboxMsg.Topic);
        Assert.Equal(FixedNow, outboxMsg.CreatedAt);

        var envelopeNode = JsonNode.Parse(outboxMsg.Payload);
        Assert.NotNull(envelopeNode);
        Assert.Equal("InventoryAssetAvailabilityChanged", envelopeNode["messageType"]?.GetValue<string>());
        Assert.Equal(asset.Id.ToString("D"), envelopeNode["aggregateId"]?.GetValue<string>());
    }

    [Fact]
    public async Task IdempotentReplay_Returns200OK_WithSameAllocationId_AndNoAdditionalOutbox()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        string extRef = $"REF-REPLAY-{Guid.NewGuid():N}";
        var holdPayload = new
        {
            sourceSystem = "rental-platform",
            externalReference = extRef,
            startDate = FixedToday.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(3).ToString("O"),
            notes = "Same payload"
        };

        // Lần 1: 201 Created
        var resp1 = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        var created = await resp1.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(created);

        // Lần 2: 200 OK replay
        var resp2 = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var replayed = await resp2.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(created.Id, replayed.Id);

        // Không sinh thêm outbox message
        await using var db = CreateDbContext();
        var outboxList = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
        Assert.Single(outboxList);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentPayload_Returns409Conflict()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        string extRef = $"REF-MISMATCH-{Guid.NewGuid():N}";
        var originalPayload = new
        {
            sourceSystem = "rental-platform",
            externalReference = extRef,
            startDate = FixedToday.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(3).ToString("O"),
            notes = "Original notes"
        };

        var resp1 = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(originalPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);

        // Payload khác ngày
        var differentDatePayload = new
        {
            sourceSystem = "rental-platform",
            externalReference = extRef,
            startDate = FixedToday.AddDays(11).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(3).ToString("O"),
            notes = "Original notes"
        };
        var resp2 = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(differentDatePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
        var problem = await resp2.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", problem.Title);
    }

    [Fact]
    public async Task InvalidInputs_AndUnknownProperties_RejectedWith400()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. Unknown property bị Disallow
        var unknownPropJson = JsonSerializer.Serialize(new
        {
            sourceSystem = "rental",
            externalReference = "REF-UNK",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O"),
            unexpectedField = "hacker_payload"
        });
        var respUnknown = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(unknownPropJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, respUnknown.StatusCode);

        // 2. startDate > endDate
        var invalidOrderJson = JsonSerializer.Serialize(new
        {
            sourceSystem = "rental",
            externalReference = "REF-ORDER",
            startDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        });
        var respOrder = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(invalidOrderJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, respOrder.StatusCode);

        // 3. Expiry in past
        var pastExpiryJson = JsonSerializer.Serialize(new
        {
            sourceSystem = "rental",
            externalReference = "REF-PAST",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(-1).ToString("O")
        });
        var respPast = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(pastExpiryJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, respPast.StatusCode);
    }

    [Fact]
    public async Task ConflictOverlap_Returns409_AndDoesNotLeakConflictData()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. Seed existing block với private notes và reason
        await using (var db = CreateDbContext())
        {
            var block = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, FixedToday.AddDays(10), FixedToday.AddDays(15),
                CalendarBlockReason.Maintenance, ownerId.ToString("D"), FixedNow, FixedToday, "Confidential inspection").Value;
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
        }

        var overlapPayload = new
        {
            sourceSystem = "rental",
            externalReference = "REF-OVERLAP-1",
            startDate = FixedToday.AddDays(12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        };

        var response = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(overlapPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("ASSET_BLOCKED", problem.Title);
        // Không chứa thông tin chi tiết lịch hoặc metadata bị lộ
        Assert.DoesNotContain("Confidential inspection", problem.Detail);
        Assert.DoesNotContain(ownerId.ToString("D"), problem.Detail);
    }

    [Fact]
    public async Task Allocate_Success_Returns200OK_AndReplayReturns200OK()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. Tạo Hold
        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = $"REF-FOR-ALLOC-{Guid.NewGuid():N}",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        };
        var holdResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, holdResp.StatusCode);
        var hold = await holdResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(hold);

        // 2. Allocate với operationKey
        const string opKey = "OP-ALLOCATE-1";
        var allocPayload = new { operationKey = opKey };
        var allocResp1 = await client.PostAsync($"/api/v1/inventory/allocations/{hold.Id}/allocate",
            new StringContent(JsonSerializer.Serialize(allocPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, allocResp1.StatusCode);

        var allocated = await allocResp1.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(allocated);
        Assert.Equal("Allocated", allocated.Status);
        Assert.Equal(hold.Id, allocated.Id);

        // 3. Replay Allocate với cùng operationKey -> 200 OK
        var allocResp2 = await client.PostAsync($"/api/v1/inventory/allocations/{hold.Id}/allocate",
            new StringContent(JsonSerializer.Serialize(allocPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, allocResp2.StatusCode);
        var replayedAlloc = await allocResp2.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(replayedAlloc);
        Assert.Equal("Allocated", replayedAlloc.Status);

        // 4. Allocate với key mới trên allocation đã Allocated -> 409 Conflict
        var allocRespDiffKey = await client.PostAsync($"/api/v1/inventory/allocations/{hold.Id}/allocate",
            new StringContent(JsonSerializer.Serialize(new { operationKey = "OP-NEW-KEY" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, allocRespDiffKey.StatusCode);
        var problem = await allocRespDiffKey.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("INVALID_STATUS_TRANSITION", problem.Title);
    }

    [Fact]
    public async Task Allocate_ExpiredHold_Returns409Conflict()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. Tạo Hold hết hạn sau 1 giờ
        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = $"REF-EXP-{Guid.NewGuid():N}",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(1).ToString("O")
        };
        var holdResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, holdResp.StatusCode);
        var hold = await holdResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(hold);

        // 2. Đồng hồ vượt quá 1 giờ
        clock.Advance(TimeSpan.FromHours(2));

        // 3. Allocate gọi trên hold đã hết hạn -> 409 Conflict (HOLD_EXPIRED)
        var allocResp = await client.PostAsync($"/api/v1/inventory/allocations/{hold.Id}/allocate",
            new StringContent(JsonSerializer.Serialize(new { operationKey = "OP-LATE" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, allocResp.StatusCode);

        var problem = await allocResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("HOLD_EXPIRED", problem.Title);
    }

    [Fact]
    public async Task Allocate_NotFound_Returns404()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        var allocResp = await client.PostAsync($"/api/v1/inventory/allocations/{Guid.NewGuid()}/allocate",
            new StringContent(JsonSerializer.Serialize(new { operationKey = "OP-MISSING" }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, allocResp.StatusCode);

        var problem = await allocResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("ALLOCATION_NOT_FOUND", problem.Title);
    }
    [Fact]
    public async Task CreateHold_SubMicrosecondTicksAboveNow_RejectedWith400_AndLeavesNoDatabaseRecord()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // Expiry is now + 1 tick (100 ns). Must be normalized to now and rejected.
        var subMicrosecondExpiry = FixedNow.AddTicks(1);
        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = "REF-TICK-HTTP",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = subMicrosecondExpiry.ToString("O")
        };

        var response = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("HOLD_EXPIRES_IN_PAST", problem.Title);

        await using var db = CreateDbContext();
        Assert.Empty(await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync());
        Assert.Empty(await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync());
    }

    [Fact]
    public async Task CreateHold_BookingsDisabled_Returns400()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, acceptingBookings: false);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = "REF-NOBOOK-HTTP",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        };

        var response = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("ASSET_NOT_ACCEPTING_BOOKINGS", problem.Title);
    }

    [Fact]
    public async Task CreateHold_IdempotentReplay_DifferentTimezoneOffset_Succeeds200()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        string extRef = $"REF-TZ-{Guid.NewGuid():N}";
        var expiryUtc = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        // Lần 1: gửi với offset +00:00 -> 201 Created
        var payloadUtc = new
        {
            sourceSystem = "rental",
            externalReference = extRef,
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = expiryUtc.ToString("O")
        };
        var resp1 = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(payloadUtc), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        var created = await resp1.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(created);

        // Lần 2: gửi cùng instant nhưng với offset +07:00 (19:00:00+07:00) -> 200 OK Replay
        var expiryIct = new DateTimeOffset(2026, 10, 2, 19, 0, 0, TimeSpan.FromHours(7));
        var payloadIct = new
        {
            sourceSystem = "rental",
            externalReference = extRef,
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = expiryIct.ToString("O")
        };
        var resp2 = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(payloadIct), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var replayed = await resp2.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(replayed);
        Assert.Equal(created.Id, replayed.Id);
    }

    [Fact]
    public async Task Allocate_Forbidden_WhenHumanOrAdminOrLacksScope()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var allocId = Guid.NewGuid();
        var allocPayload = new { operationKey = "OP-AUTH-TEST" };
        var content = new StringContent(JsonSerializer.Serialize(allocPayload), Encoding.UTF8, "application/json");

        // 1. Human owner -> 403
        var humanToken = factory.CreateHumanToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);
        var res1 = await client.PostAsync($"/api/v1/inventory/allocations/{allocId}/allocate", content);
        Assert.Equal(HttpStatusCode.Forbidden, res1.StatusCode);

        // 2. Admin -> 403
        var adminToken = factory.CreateAdminToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var res2 = await client.PostAsync($"/api/v1/inventory/allocations/{allocId}/allocate", content);
        Assert.Equal(HttpStatusCode.Forbidden, res2.StatusCode);

        // 3. Service lacking scope -> 403
        var svcToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ["inventory.calendar.read"]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", svcToken);
        var res3 = await client.PostAsync($"/api/v1/inventory/allocations/{allocId}/allocate", content);
        Assert.Equal(HttpStatusCode.Forbidden, res3.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Allocate_InvalidOperationKey_RejectedWith400(string opKey)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        var allocPayload = new { operationKey = opKey };
        var resp = await client.PostAsync($"/api/v1/inventory/allocations/{Guid.NewGuid()}/allocate",
            new StringContent(JsonSerializer.Serialize(allocPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Allocate_UnknownProperties_RejectedWith400()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        var unknownJson = JsonSerializer.Serialize(new
        {
            operationKey = "OP-VALID",
            unexpectedProperty = "disallowed"
        });
        var resp = await client.PostAsync($"/api/v1/inventory/allocations/{Guid.NewGuid()}/allocate",
            new StringContent(unknownJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Allocate_BookingsDisabledOnAsset_StillSucceeds200()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, acceptingBookings: true);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. Tạo Hold khi đang bật bookings
        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = $"REF-NOBOOK-ALLOC-{Guid.NewGuid():N}",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        };
        var holdResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, holdResp.StatusCode);
        var hold = await holdResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(hold);

        // 2. Tắt bookings trên asset
        await using (var db = CreateDbContext())
        {
            var trackedAsset = await db.Assets.FindAsync(asset.Id);
            Assert.NotNull(trackedAsset);
            trackedAsset.DisableBookings("Suspended", FixedNow);
            await db.SaveChangesAsync();
        }

        // 3. Allocate phải thành công 200 OK (Allocate không kiểm tra AcceptingBookings)
        var allocPayload = new { operationKey = "OP-NOBOOK-SUCCESS" };
        var allocResp = await client.PostAsync($"/api/v1/inventory/allocations/{hold.Id}/allocate",
            new StringContent(JsonSerializer.Serialize(allocPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, allocResp.StatusCode);
        var allocated = await allocResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(allocated);
        Assert.Equal("Allocated", allocated.Status);
    }

    [Fact]
    public async Task Allocate_TerminalState_Released_Returns409Conflict()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAllocationWrite]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // 1. Tạo Hold
        var holdPayload = new
        {
            sourceSystem = "rental",
            externalReference = $"REF-TERM-{Guid.NewGuid():N}",
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            holdExpiresAt = FixedNow.AddHours(2).ToString("O")
        };
        var holdResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/holds",
            new StringContent(JsonSerializer.Serialize(holdPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, holdResp.StatusCode);
        var hold = await holdResp.Content.ReadFromJsonAsync<AllocationResponse>();
        Assert.NotNull(hold);

        // 2. Chuyển hold sang Released trực tiếp trong DB
        await using (var db = CreateDbContext())
        {
            var trackedAlloc = await db.AssetAllocations.Include(a => a.Transitions).SingleAsync(a => a.Id == hold.Id);
            Assert.True(trackedAlloc.ReleaseHold("Cancelled by renter", FixedNow.AddHours(1), "system").IsSuccess);
            await db.SaveChangesAsync();
        }

        // 3. Gọi Allocate trên Released hold -> 409 Conflict (ALLOCATION_IN_TERMINAL_STATE)
        var allocPayload = new { operationKey = "OP-TERM-ALLOC" };
        var allocResp = await client.PostAsync($"/api/v1/inventory/allocations/{hold.Id}/allocate",
            new StringContent(JsonSerializer.Serialize(allocPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, allocResp.StatusCode);
        var problem = await allocResp.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("ALLOCATION_IN_TERMINAL_STATE", problem.Title);
    }
}
