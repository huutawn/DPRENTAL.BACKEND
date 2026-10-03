using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Kiểm thử HTTP Endpoints với JWT/JWKS test infrastructure, clock cố định và PostgreSQL thật theo DPR-149.
/// </summary>
public sealed class CalendarHttpIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);

    public CalendarHttpIntegrationTests(PostgresFixture fixture)
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

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId, Guid orgId, bool acceptingBookings = true)
    {
        await using var db = CreateDbContext();
        var asset = InventoryAsset.Create(
            Guid.NewGuid(),
            ownerUserId,
            Guid.NewGuid(),
            "Sony FX3",
            1,
            "{}",
            FixedNow).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended for test", FixedNow);
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    [Fact]
    public async Task Owner_WithoutPermission_CanCreateReadAndDelete_WithStrictOutboxContract()
    {
        var clock = new TestTimeProvider(FixedNow);
        await using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        // Token không có claim 'p' (permissions = null)
        var ownerToken = factory.CreateHumanToken(ownerId, permissions: null, orgId: orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 1. POST tạo calendar block
        var postPayload = new
        {
            startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "Maintenance",
            note = "Bảo dưỡng định kỳ"
        };
        var postContent = new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json");

        var postResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", postContent);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var createdBlock = await postResp.Content.ReadFromJsonAsync<CalendarBlockResponse>();
        Assert.NotNull(createdBlock);
        Assert.NotEqual(Guid.Empty, createdBlock.Id);
        Assert.Equal(asset.Id, createdBlock.AssetId);
        Assert.Equal(FixedToday.AddDays(5), createdBlock.StartDate);
        Assert.Equal(FixedToday.AddDays(7), createdBlock.EndDate);
        Assert.Equal(CalendarBlockReason.Maintenance, createdBlock.Reason);
        Assert.Equal("Bảo dưỡng định kỳ", createdBlock.Note);
        Assert.Equal(FixedNow, createdBlock.CreatedAt);

        // Kiểm tra Outbox event được tạo cùng lúc trong PostgreSQL với contract nghiêm ngặt
        await using (var db = CreateDbContext())
        {
            var outboxMessages = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Single(outboxMessages);
            var outboxMsg = outboxMessages[0];

            Assert.Equal("inventory.asset.events.v1", outboxMsg.Topic);
            Assert.Equal(asset.Id.ToString("D"), outboxMsg.KafkaKey);
            Assert.Equal(FixedNow, outboxMsg.CreatedAt);

            // Parse wire JSON của MessageEnvelope
            var envelopeNode = JsonNode.Parse(outboxMsg.Payload);
            Assert.NotNull(envelopeNode);
            Assert.Equal("InventoryAssetAvailabilityChanged", envelopeNode["messageType"]?.GetValue<string>());
            Assert.Equal(1, envelopeNode["version"]?.GetValue<int>());
            Assert.Equal(asset.Id.ToString("D"), envelopeNode["aggregateId"]?.GetValue<string>());
            Assert.Null(envelopeNode["organizationId"]);
            Assert.Equal(FixedNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), envelopeNode["occurredAt"]?.GetValue<string>());

            // Kiểm tra Payload đúng 2 thuộc tính: assetId và changedAt, không lộ bất kỳ metadata nào khác
            var payloadNode = envelopeNode["payload"]?.AsObject();
            Assert.NotNull(payloadNode);
            Assert.Equal(2, payloadNode.Count);
            Assert.Equal(asset.Id.ToString("D"), payloadNode["assetId"]?.GetValue<string>());
            Assert.Equal(FixedNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture), payloadNode["changedAt"]?.GetValue<string>());
            Assert.Null(payloadNode["note"]);
            Assert.Null(payloadNode["reason"]);
            Assert.Null(payloadNode["userId"]);
        }

        // 2. GET đọc calendar
        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var calendar = await getResp.Content.ReadFromJsonAsync<CalendarResponse>();
        Assert.NotNull(calendar);
        Assert.Equal(asset.Id, calendar.AssetId);
        Assert.Equal(FixedToday, calendar.StartDate);
        Assert.Equal(FixedToday.AddMonths(6), calendar.EndDate);
        Assert.Single(calendar.Entries);
        var entry = calendar.Entries[0];
        Assert.Equal(createdBlock.Id, entry.Id);
        Assert.Equal("CalendarBlock", entry.Type);
        Assert.Equal(FixedToday.AddDays(5), entry.StartDate);
        Assert.Equal(FixedToday.AddDays(7), entry.EndDate);
        Assert.Equal("Maintenance", entry.Reason);
        Assert.Equal("Bảo dưỡng định kỳ", entry.Note);
        Assert.Null(entry.HoldExpiresAt);

        // 3. DELETE xóa calendar block
        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{createdBlock.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // 4. GET lại -> entries rỗng
        var getAfterDel = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.OK, getAfterDel.StatusCode);
        var calAfterDel = await getAfterDel.Content.ReadFromJsonAsync<CalendarResponse>();
        Assert.NotNull(calAfterDel);
        Assert.Empty(calAfterDel.Entries);

        // Kiểm tra đúng 2 events (1 create, 1 delete)
        await using (var db = CreateDbContext())
        {
            var totalOutbox = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Equal(2, totalOutbox.Count);
        }

        // 5. DELETE lần 2 -> 404 và KHÔNG sinh event mới
        var secondDel = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{createdBlock.Id}");
        Assert.Equal(HttpStatusCode.NotFound, secondDel.StatusCode);

        await using (var db = CreateDbContext())
        {
            var totalOutboxAfterFailedDel = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Equal(2, totalOutboxAfterFailedDel.Count);
        }
    }

    [Fact]
    public async Task Admin_WhoIsOwner_CanAccess_AllThreeApis()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        // Token Admin nhưng sub trùng OwnerUserId
        var adminOwnerToken = factory.CreateAdminToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminOwnerToken);

        // GET
        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        // POST
        var postPayload = new
        {
            startDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "OwnerReserved"
        };
        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
        var block = await postResp.Content.ReadFromJsonAsync<CalendarBlockResponse>();
        Assert.NotNull(block);

        // DELETE
        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{block.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
    }

    [Fact]
    public async Task Admin_WhoIsNotOwner_IsForbidden_OnAllThreeApis()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var otherAdminId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        // Admin khác owner
        var adminToken = factory.CreateAdminToken(otherAdminId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // GET -> 403
        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.Forbidden, getResp.StatusCode);

        // POST -> 403
        var postPayload = new
        {
            startDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "Maintenance"
        };
        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, postResp.StatusCode);

        // DELETE -> 403
        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delResp.StatusCode);
    }

    [Fact]
    public async Task ServicePrincipal_IsForbidden_OnAllThreeApis()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ["inventory.calendar.read", "inventory.allocation.write"]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        // GET -> 403
        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.Forbidden, getResp.StatusCode);

        // POST -> 403
        var postPayload = new
        {
            startDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "Maintenance"
        };
        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, postResp.StatusCode);

        // DELETE -> 403
        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delResp.StatusCode);
    }

    [Fact]
    public async Task SameOrgNonOwner_IsForbidden_OnAllThreeApis()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var colleagueId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        // Cùng organization nhưng khác UserId
        var colleagueToken = factory.CreateHumanToken(colleagueId, orgId: orgId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", colleagueToken);

        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.Forbidden, getResp.StatusCode);

        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"Maintenance\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, postResp.StatusCode);

        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, delResp.StatusCode);
    }

    [Fact]
    public async Task Owner_CanCreateReadDelete_WhenAcceptingBookingsFalse()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        // Asset có AcceptingBookings = false
        var asset = await SeedAssetAsync(ownerId, orgId, acceptingBookings: false);

        var ownerToken = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);

        // 1. POST tạo block thành công ngay cả khi AcceptingBookings = false
        var postPayload = new
        {
            startDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "Maintenance",
            note = "Khóa vận hành khi tạm dừng booking"
        };
        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);

        var block = await postResp.Content.ReadFromJsonAsync<CalendarBlockResponse>();
        Assert.NotNull(block);

        // 2. GET đọc calendar thành công
        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var cal = await getResp.Content.ReadFromJsonAsync<CalendarResponse>();
        Assert.NotNull(cal);
        Assert.Single(cal.Entries);

        // 3. DELETE block thành công
        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{block.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);
    }

    [Fact]
    public async Task MissingOrInvalidToken_Returns401()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var assetId = Guid.NewGuid();

        // Không có header Authorization
        var noAuthResp = await client.GetAsync($"/api/v1/inventory/assets/{assetId}/calendar");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuthResp.StatusCode);

        // Token rác
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.jwt.token");
        var invalidAuthResp = await client.GetAsync($"/api/v1/inventory/assets/{assetId}/calendar");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidAuthResp.StatusCode);
    }

    [Fact]
    public async Task NonExistentAsset_Returns404_OnAllThreeApis()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var missingAssetId = Guid.NewGuid();
        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var getResp = await client.GetAsync($"/api/v1/inventory/assets/{missingAssetId}/calendar");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        var postPayload = new
        {
            startDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "Maintenance"
        };
        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{missingAssetId}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, postResp.StatusCode);

        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{missingAssetId}/calendar-blocks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, delResp.StatusCode);
    }

    [Fact]
    public async Task BlockOnOtherAsset_Returns404_EvenIfSameOwner()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset1 = await SeedAssetAsync(ownerId, orgId);
        var asset2 = await SeedAssetAsync(ownerId, orgId);

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Tạo block trên Asset 1
        var postPayload = new
        {
            startDate = FixedToday.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = FixedToday.AddDays(12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            reason = "Maintenance"
        };
        var postResp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset1.Id}/calendar-blocks",
            new StringContent(JsonSerializer.Serialize(postPayload), Encoding.UTF8, "application/json"));
        var createdBlock = await postResp.Content.ReadFromJsonAsync<CalendarBlockResponse>();
        Assert.NotNull(createdBlock);

        // Cố xóa block của Asset 1 nhưng gọi route của Asset 2 -> 404
        var delResp = await client.DeleteAsync($"/api/v1/inventory/assets/{asset2.Id}/calendar-blocks/{createdBlock.Id}");
        Assert.Equal(HttpStatusCode.NotFound, delResp.StatusCode);

        // Xóa trên Asset 1 thành công
        var validDel = await client.DeleteAsync($"/api/v1/inventory/assets/{asset1.Id}/calendar-blocks/{createdBlock.Id}");
        Assert.Equal(HttpStatusCode.NoContent, validDel.StatusCode);
    }

    [Theory]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":1}", "Numeric reason rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"1\"}", "Numeric string '1' rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"+1\"}", "Signed numeric string '+1' rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"Maintenance, OwnerReserved\"}", "Composite enum string rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"\"}", "Empty reason rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"   \"}", "Whitespace reason rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"UnknownReason\"}", "Invalid reason string rejected")]
    [InlineData("{\"startDate\":\"2026/10/10\",\"endDate\":\"2026-10-12\",\"reason\":\"Maintenance\"}", "Invalid date format rejected")]
    [InlineData("{\"endDate\":\"2026-10-12\",\"reason\":\"Maintenance\"}", "Missing startDate rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"reason\":\"Maintenance\"}", "Missing endDate rejected")]
    [InlineData("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\"}", "Missing reason rejected")]
    public async Task InvalidInputs_RejectedWith400(string json, string scenario)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, Guid.NewGuid());
        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest, $"Scenario '{scenario}' expected 400 BadRequest but got {resp.StatusCode}");
    }

    [Fact]
    public async Task DomainBoundary_ValidInputs_AndRejections_Enforced()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, Guid.NewGuid());
        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Ngày bắt đầu trong quá khứ -> 400
        var pastPayload = new { startDate = FixedToday.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = "Maintenance" };
        var pastResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", new StringContent(JsonSerializer.Serialize(pastPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, pastResp.StatusCode);

        // 2. Ngày đảo thứ tự (startDate > endDate) -> 400
        var reversedPayload = new { startDate = FixedToday.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = "Maintenance" };
        var revResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", new StringContent(JsonSerializer.Serialize(reversedPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, revResp.StatusCode);

        // 3. Ngày kết thúc vượt quá 6 tháng (chân trời) -> 400
        var pastHorizon = FixedToday.AddMonths(6).AddDays(1);
        var horizonPayload = new { startDate = FixedToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = pastHorizon.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = "Maintenance" };
        var horizResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", new StringContent(JsonSerializer.Serialize(horizonPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, horizResp.StatusCode);

        // 4. Note vượt quá 500 ký tự (501 ký tự) -> 400
        var longNotePayload = new { startDate = FixedToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = FixedToday.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = "Maintenance", note = new string('x', 501) };
        var noteResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", new StringContent(JsonSerializer.Serialize(longNotePayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noteResp.StatusCode);

        // 5. Note đúng 500 ký tự -> 201 Created!
        var exact500Payload = new { startDate = FixedToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = FixedToday.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = "maintenance", note = new string('y', 500) };
        var ok500Resp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", new StringContent(JsonSerializer.Serialize(exact500Payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, ok500Resp.StatusCode);
        var created500 = await ok500Resp.Content.ReadFromJsonAsync<CalendarBlockResponse>();
        Assert.NotNull(created500);
        Assert.Equal(500, created500.Note?.Length);

        // 6. Reason case-insensitive và trim: "   Inspection   " -> 201 Created
        var trimPayload = new { startDate = FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), endDate = FixedToday.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), reason = "   Inspection   ", note = "   trim note   " };
        var trimResp = await client.PostAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks", new StringContent(JsonSerializer.Serialize(trimPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, trimResp.StatusCode);
        var createdTrim = await trimResp.Content.ReadFromJsonAsync<CalendarBlockResponse>();
        Assert.NotNull(createdTrim);
        Assert.Equal(CalendarBlockReason.Inspection, createdTrim.Reason);
        Assert.Equal("trim note", createdTrim.Note);
    }

    [Fact]
    public async Task CalendarRead_SnapshotWindowAndPrivacy_Enforced()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Seed:
        // 1. Block bên trong window: [FixedToday + 10, FixedToday + 12]
        // 2. Active Hold bên trong window: [FixedToday + 15, FixedToday + 18]
        // 3. Expired Hold (holdExpiresAt < now): [FixedToday + 20, FixedToday + 22] -> BỊ LOẠI
        // 4. Exact Expiry Hold (holdExpiresAt == now): [FixedToday + 23, FixedToday + 24] -> BỊ LOẠI
        // 5. Allocated bên trong window: [FixedToday + 25, FixedToday + 28]
        // 6. Spanning block: bắt đầu trong quá khứ, kết thúc trong window: [FixedToday - 2, FixedToday + 2] -> ĐƯỢC CHỌN, GIỮ NGUYÊN NGÀY GỐC
        // 7. Block kết thúc hôm qua: [FixedToday - 5, FixedToday - 1] -> BỊ LOẠI
        // 8. Block ngoài window (> 6 tháng): [FixedToday + 7 tháng, ...] -> BỊ LOẠI
        await using (var db = CreateDbContext())
        {
            var trackedAsset = await db.Assets.FindAsync(asset.Id);
            Assert.NotNull(trackedAsset);

            // Block kết thúc hôm qua
            var yesterdayBlock = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, FixedToday.AddDays(-5), FixedToday.AddDays(-1),
                CalendarBlockReason.Maintenance, ownerId.ToString("D"), FixedNow.AddDays(-10), FixedToday.AddDays(-10)).Value;

            // Spanning block: [FixedToday - 2, FixedToday + 2]
            var spanningBlock = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, FixedToday.AddDays(-2), FixedToday.AddDays(2),
                CalendarBlockReason.Inspection, ownerId.ToString("D"), FixedNow.AddDays(-5), FixedToday.AddDays(-5), "Spanning note").Value;

            // Block bên trong window: [FixedToday + 10, FixedToday + 12]
            var block1 = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, FixedToday.AddDays(10), FixedToday.AddDays(12),
                CalendarBlockReason.Maintenance, ownerId.ToString("D"), FixedNow, FixedToday, "Internal maintenance note").Value;

            // Active Hold
            var activeHold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, new RentalPeriod(FixedToday.AddDays(15), FixedToday.AddDays(18)),
                FixedNow.AddHours(5), FixedNow, "rental-sys", "EXT-ACTIVE-HOLD", "Private hold note").Value;

            // Expired Hold (holdExpiresAt < now)
            var expiredHold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, new RentalPeriod(FixedToday.AddDays(20), FixedToday.AddDays(22)),
                FixedNow.AddHours(-1), FixedNow.AddHours(-5), "rental-sys", "EXT-EXPIRED-HOLD", "Private expired note").Value;

            // Exact Expiry Hold (holdExpiresAt == now)
            var exactExpiryHold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, new RentalPeriod(FixedToday.AddDays(23), FixedToday.AddDays(24)),
                FixedNow, FixedNow.AddHours(-1), "rental-sys", "EXT-EXACT-EXPIRY-HOLD", "Private exact note").Value;

            // Allocated
            var allocated = AssetAllocation.CreateManual(
                Guid.NewGuid(), asset.Id, new RentalPeriod(FixedToday.AddDays(25), FixedToday.AddDays(28)),
                FixedNow, "EXT-ALLOCATED").Value;

            // Block ngoài window (> 6 tháng)
            var farBlock = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, FixedToday.AddMonths(7), FixedToday.AddMonths(7).AddDays(5),
                CalendarBlockReason.Other, ownerId.ToString("D"), FixedNow, FixedToday, "Far note", maxMonthsAhead: 12).Value;

            db.CalendarBlocks.AddRange(yesterdayBlock, spanningBlock, block1, farBlock);
            db.AssetAllocations.AddRange(activeHold, expiredHold, exactExpiryHold, allocated);
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/calendar");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var jsonString = await resp.Content.ReadAsStringAsync();
        var jsonNode = JsonNode.Parse(jsonString);
        Assert.NotNull(jsonNode);

        var entries = jsonNode["entries"]?.AsArray();
        Assert.NotNull(entries);

        // Chỉ có đúng 4 entries: spanningBlock, block1, activeHold, allocated!
        // yesterdayBlock, expiredHold, exactExpiryHold, farBlock đều bị loại!
        Assert.Equal(4, entries.Count);

        // Sắp xếp ổn định theo startDate, endDate, type, id:
        // Entry 0: spanningBlock [FixedToday - 2, FixedToday + 2] (Giữ nguyên ngày gốc, không bị cắt!)
        var e0 = entries[0]!;
        Assert.Equal("CalendarBlock", e0["type"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e0["startDate"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e0["endDate"]?.GetValue<string>());
        Assert.Equal("Inspection", e0["reason"]?.GetValue<string>());
        Assert.Equal("Spanning note", e0["note"]?.GetValue<string>());
        Assert.Null(e0["holdExpiresAt"]);

        // Entry 1: block1 [FixedToday + 10, FixedToday + 12]
        var e1 = entries[1]!;
        Assert.Equal("CalendarBlock", e1["type"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e1["startDate"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e1["endDate"]?.GetValue<string>());
        Assert.Equal("Maintenance", e1["reason"]?.GetValue<string>());
        Assert.Equal("Internal maintenance note", e1["note"]?.GetValue<string>());

        // Entry 2: activeHold [FixedToday + 15, FixedToday + 18]
        var e2 = entries[2]!;
        Assert.Equal("Held", e2["type"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(15).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e2["startDate"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(18).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e2["endDate"]?.GetValue<string>());
        Assert.NotNull(e2["holdExpiresAt"]);
        Assert.Null(e2["reason"]);
        Assert.Null(e2["note"]);

        // Entry 3: allocated [FixedToday + 25, FixedToday + 28]
        var e3 = entries[3]!;
        Assert.Equal("Allocated", e3["type"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(25).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e3["startDate"]?.GetValue<string>());
        Assert.Equal(FixedToday.AddDays(28).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e3["endDate"]?.GetValue<string>());
        Assert.Null(e3["reason"]);
        Assert.Null(e3["note"]);
        Assert.Null(e3["holdExpiresAt"]);

        // Privacy: Không lộ metadata allocation
        Assert.DoesNotContain("EXT-ACTIVE-HOLD", jsonString);
        Assert.DoesNotContain("Private hold note", jsonString);
        Assert.DoesNotContain("EXT-EXPIRED-HOLD", jsonString);
        Assert.DoesNotContain("EXT-ALLOCATED", jsonString);
    }

    [Fact]
    public async Task RejectedRequests_DoNotMutateDatabaseOrOutbox()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);

        int initialBlockCount;
        int initialOutboxCount;
        await using (var db = CreateDbContext())
        {
            initialBlockCount = await db.CalendarBlocks.CountAsync(b => b.AssetId == asset.Id);
            initialOutboxCount = await db.OutboxMessages.CountAsync();
        }

        var token = factory.CreateHumanToken(ownerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 400 Bad Request
        var badReq = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent("{\"invalid\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badReq.StatusCode);

        // 403 Forbidden
        var attackerToken = factory.CreateHumanToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", attackerToken);
        var forbidReq = await client.PostAsync(
            $"/api/v1/inventory/assets/{asset.Id}/calendar-blocks",
            new StringContent("{\"startDate\":\"2026-10-10\",\"endDate\":\"2026-10-12\",\"reason\":\"Maintenance\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, forbidReq.StatusCode);

        // 404 NotFound
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var notFoundReq = await client.DeleteAsync($"/api/v1/inventory/assets/{asset.Id}/calendar-blocks/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, notFoundReq.StatusCode);

        // Database không có thay đổi
        await using (var db = CreateDbContext())
        {
            var finalBlockCount = await db.CalendarBlocks.CountAsync(b => b.AssetId == asset.Id);
            var finalOutboxCount = await db.OutboxMessages.CountAsync();

            Assert.Equal(initialBlockCount, finalBlockCount);
            Assert.Equal(initialOutboxCount, finalOutboxCount);
        }
    }
}
