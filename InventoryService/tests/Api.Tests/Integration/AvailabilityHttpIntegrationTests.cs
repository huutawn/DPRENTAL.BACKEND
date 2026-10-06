using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Availability;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class AvailabilityHttpIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] ScopeCalendarReadArray = ["inventory.calendar.read"];
    private static readonly string[] ScopeAllocationWriteArray = ["inventory.allocation.write"];
    private static readonly string[] ScopeAvailabilityReadArray = ["inventory.availability.read"];
    public AvailabilityHttpIntegrationTests(PostgresFixture fixture)
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
        var assetId = Guid.NewGuid();
        var rate = AssetRentalRate.Create(assetId, 100_000m, null, null, "VND", FixedNow).Value;
        var asset = InventoryAsset.Create(
            assetId,
            ownerUserId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Sony FX3",
            1,
            "{}",
            FixedNow,
            rate).Value;

        if (!acceptingBookings)
        {
            asset.DisableBookings("Suspended for test", FixedNow);
        }

        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    [Fact]
    public async Task CheckAvailability_Endpoint_CorrectRouteAndContract_NoPrivateFields()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        // Human token
        var token = factory.CreateHumanToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var requestBody = new CheckAvailabilityBatchRequest(
            new[] { asset.Id, Guid.NewGuid() },
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 15));

        using var response = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", requestBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Contract must contain exactly unavailableAssetIds and notFoundAssetIds (NO availableAssetIds)
        Assert.False(root.TryGetProperty("availableAssetIds", out _));
        Assert.True(root.TryGetProperty("unavailableAssetIds", out var unavail));
        Assert.True(root.TryGetProperty("notFoundAssetIds", out var notFound));
        Assert.Equal(2, root.EnumerateObject().Count());

        // Contract does not expose private fields
        Assert.False(root.TryGetProperty("reason", out _));
        Assert.False(root.TryGetProperty("note", out _));
        Assert.False(root.TryGetProperty("notes", out _));
        Assert.False(root.TryGetProperty("externalReference", out _));

        var notFoundList = notFound.EnumerateArray().Select(x => x.GetGuid()).ToList();
        Assert.Contains(notFoundList, id => id != asset.Id);
    }
    [Fact]
    public async Task CheckAvailability_Endpoint_AuthorizationMatrix()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var requestBody = new CheckAvailabilityBatchRequest(
            new[] { Guid.NewGuid() },
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 15));

        // 1. Unauthenticated -> 401
        using var noAuthResp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", requestBody);
        Assert.Equal(HttpStatusCode.Unauthorized, noAuthResp.StatusCode);

        // 2. Service token with old scope inventory.calendar.read only -> 403
        var calReadServiceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ScopeCalendarReadArray);
        using var calReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/availability/check")
        {
            Content = JsonContent.Create(requestBody)
        };
        calReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", calReadServiceToken);
        using var calResp = await client.SendAsync(calReq);
        Assert.Equal(HttpStatusCode.Forbidden, calResp.StatusCode);

        // 3. Service token with write-only scope inventory.allocation.write -> 403
        var allocWriteToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ScopeAllocationWriteArray);
        using var writeReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/availability/check")
        {
            Content = JsonContent.Create(requestBody)
        };
        writeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", allocWriteToken);
        using var writeResp = await client.SendAsync(writeReq);
        Assert.Equal(HttpStatusCode.Forbidden, writeResp.StatusCode);

        // 4. Human authenticated token -> 200
        var humanToken = factory.CreateHumanToken(Guid.NewGuid());
        using var humanReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/availability/check")
        {
            Content = JsonContent.Create(requestBody)
        };
        humanReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", humanToken);
        using var humanResp = await client.SendAsync(humanReq);
        Assert.Equal(HttpStatusCode.OK, humanResp.StatusCode);

        // 5. Service token with inventory.availability.read -> 200
        var availServiceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ScopeAvailabilityReadArray);
        using var svcReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/availability/check")
        {
            Content = JsonContent.Create(requestBody)
        };
        svcReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", availServiceToken);
        using var svcResp = await client.SendAsync(svcReq);
        Assert.Equal(HttpStatusCode.OK, svcResp.StatusCode);
    }

    [Fact]
    public async Task CheckAvailability_Endpoint_ValidationErrors()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = factory.CreateHumanToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Empty IDs -> 400
        var emptyReq = new CheckAvailabilityBatchRequest(Array.Empty<Guid>(), new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        using var emptyResp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", emptyReq);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);

        // 2. > 500 IDs -> 400
        var tooManyIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();
        var tooManyReq = new CheckAvailabilityBatchRequest(tooManyIds, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 12));
        using var tooManyResp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", tooManyReq);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyResp.StatusCode);

        // 3. Invalid date order -> 400
        var invalidOrderReq = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 10));
        using var orderResp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", invalidOrderReq);
        Assert.Equal(HttpStatusCode.BadRequest, orderResp.StatusCode);

        // 4. > 6 months horizon -> 400
        var start = new DateOnly(2026, 1, 1);
        var horizonReq = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, start, start.AddMonths(6).AddDays(1));
        using var horizonResp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", horizonReq);
        Assert.Equal(HttpStatusCode.BadRequest, horizonResp.StatusCode);

        // 5. Raw HTTP JSON missing both dates -> 400
        using var rawNoDates = new StringContent($"{{\"assetIds\":[\"{Guid.NewGuid()}\"]}}", System.Text.Encoding.UTF8, "application/json");
        using var rawNoDatesResp = await client.PostAsync("/api/v1/inventory/availability/check", rawNoDates);
        Assert.Equal(HttpStatusCode.BadRequest, rawNoDatesResp.StatusCode);

        // 6. Raw HTTP JSON null dates -> 400
        using var rawNullDates = new StringContent($"{{\"assetIds\":[\"{Guid.NewGuid()}\"],\"startDate\":null,\"endDate\":null}}", System.Text.Encoding.UTF8, "application/json");
        using var rawNullDatesResp = await client.PostAsync("/api/v1/inventory/availability/check", rawNullDates);
        Assert.Equal(HttpStatusCode.BadRequest, rawNullDatesResp.StatusCode);

        // 7. Raw HTTP JSON malformed date -> 400
        using var rawMalformed = new StringContent($"{{\"assetIds\":[\"{Guid.NewGuid()}\"],\"startDate\":\"invalid\",\"endDate\":\"2026-10-10\"}}", System.Text.Encoding.UTF8, "application/json");
        using var rawMalformedResp = await client.PostAsync("/api/v1/inventory/availability/check", rawMalformed);
        Assert.Equal(HttpStatusCode.BadRequest, rawMalformedResp.StatusCode);

        // 8. Year 9999 boundary -> 200 (Safe horizon calculation)
        var safe9999Req = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, new DateOnly(9999, 12, 1), new DateOnly(9999, 12, 2));
        using var safe9999Resp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", safe9999Req);
        Assert.Equal(HttpStatusCode.OK, safe9999Resp.StatusCode);

        // 9. Leap year boundary -> 200
        var leapReq = new CheckAvailabilityBatchRequest(new[] { Guid.NewGuid() }, new DateOnly(2024, 2, 29), new DateOnly(2024, 8, 29));
        using var leapResp = await client.PostAsJsonAsync("/api/v1/inventory/availability/check", leapReq);
        Assert.Equal(HttpStatusCode.OK, leapResp.StatusCode);
    }
    [Fact]
    public async Task GetBusyRanges_Endpoint_CorrectRouteAndContract_NoPrivateFields()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        // Block & hold
        await using (var db = CreateDbContext())
        {
            var block = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8),
                CalendarBlockReason.Maintenance, "op", FixedNow, new DateOnly(2026, 10, 5)).Value;
            var hold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, RentalPeriod.Create(new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12)).Value,
                FixedNow.AddHours(2), FixedNow, "SYS", $"REF-{Guid.NewGuid():N}").Value;
            db.CalendarBlocks.Add(block);
            db.AssetAllocations.Add(hold);
            await db.SaveChangesAsync();
        }

        var token = factory.CreateHumanToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/busy-ranges?from=2026-10-01&to=2026-10-31");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(asset.Id, root.GetProperty("assetId").GetGuid());
        Assert.True(root.GetProperty("acceptingBookings").GetBoolean());
        Assert.True(root.TryGetProperty("ranges", out var rangesElem));

        // Private fields must not exist
        Assert.False(root.TryGetProperty("reason", out _));
        Assert.False(root.TryGetProperty("notes", out _));
        Assert.False(root.TryGetProperty("note", out _));
        Assert.False(root.TryGetProperty("externalReference", out _));

        var ranges = rangesElem.EnumerateArray().ToList();
        Assert.Single(ranges);
        Assert.Equal("2026-10-05", ranges[0].GetProperty("startDate").GetString());
        Assert.Equal("2026-10-12", ranges[0].GetProperty("endDate").GetString());
    }

    [Fact]
    public async Task GetBusyRanges_Endpoint_AuthorizationAndNotFound()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);

        // 1. Unauthenticated -> 401
        using var unauthResp = await client.GetAsync($"/api/v1/inventory/assets/{asset.Id}/busy-ranges?from=2026-10-01&to=2026-10-31");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. Service token lacking availability read scope -> 403
        var calReadToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ScopeCalendarReadArray);
        using var reqCal = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/inventory/assets/{asset.Id}/busy-ranges?from=2026-10-01&to=2026-10-31");
        reqCal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", calReadToken);
        using var respCal = await client.SendAsync(reqCal);
        Assert.Equal(HttpStatusCode.Forbidden, respCal.StatusCode);

        // 3. Service with availability.read -> 200
        var availToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: ScopeAvailabilityReadArray);
        using var reqAvail = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/inventory/assets/{asset.Id}/busy-ranges?from=2026-10-01&to=2026-10-31");
        reqAvail.Headers.Authorization = new AuthenticationHeaderValue("Bearer", availToken);
        using var respAvail = await client.SendAsync(reqAvail);
        Assert.Equal(HttpStatusCode.OK, respAvail.StatusCode);

        // 4. Nonexistent asset -> 404
        using var reqNotFound = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/inventory/assets/{Guid.NewGuid()}/busy-ranges?from=2026-10-01&to=2026-10-31");
        reqNotFound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", availToken);
        using var respNotFound = await client.SendAsync(reqNotFound);
        Assert.Equal(HttpStatusCode.NotFound, respNotFound.StatusCode);

        // 5. Service with availability.read cannot access operational calendar -> 403
        using var reqOpCal = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/inventory/assets/{asset.Id}/calendar");
        reqOpCal.Headers.Authorization = new AuthenticationHeaderValue("Bearer", availToken);
        using var respOpCal = await client.SendAsync(reqOpCal);
        Assert.Equal(HttpStatusCode.Forbidden, respOpCal.StatusCode);
    }
}
