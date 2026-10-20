using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Warehouse;
using Dprental.InventoryService.Application.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed class WarehouseHttpIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    private static (InventorySecurityTestFactory factory, HttpClient client) CreateClient(PostgresFixture fixture)
    {
        var factory = new InventorySecurityTestFactory(fixture.ConnectionString);
        var client = factory.CreateClient();
        return (factory, client);
    }

    [Fact]
    public async Task Warehouse_Endpoints_EnforceAuthenticationAndAuthorization()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            var (factory, client) = CreateClient(fixture);
            await using var _ = factory;
            using var __ = client;

            var user = Guid.NewGuid();

            // 1. Anonymous GET -> 401
            var getAnon = await client.GetAsync("api/v1/inventory/warehouse/me");
            Assert.Equal(HttpStatusCode.Unauthorized, getAnon.StatusCode);

            // 2. Anonymous PUT -> 401
            var putAnon = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me",
                new UpdateWarehouseRequest("123 Le Loi", "01", "00070", 21.0, 105.8));
            Assert.Equal(HttpStatusCode.Unauthorized, putAnon.StatusCode);

            // 3. Service Token GET -> 403
            var serviceToken = factory.CreateServiceToken(Guid.NewGuid(), scopes: [InventoryAccessEvaluator.ScopeAvailabilityRead]);
            using var svcReq = new HttpRequestMessage(HttpMethod.Get, "api/v1/inventory/warehouse/me");
            svcReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            var getSvc = await client.SendAsync(svcReq);
            Assert.Equal(HttpStatusCode.Forbidden, getSvc.StatusCode);

            // 4. Service Token PUT -> 403
            using var putSvcReq = new HttpRequestMessage(HttpMethod.Put, "api/v1/inventory/warehouse/me")
            {
                Content = JsonContent.Create(new UpdateWarehouseRequest("123 Le Loi", "01", "00070", 21.0, 105.8))
            };
            putSvcReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            var putSvc = await client.SendAsync(putSvcReq);
            Assert.Equal(HttpStatusCode.Forbidden, putSvc.StatusCode);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Warehouse_Lifecycle_Create_Get_Update_Conflict_AcrossUsers()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            var (factory, client) = CreateClient(fixture);
            await using var _ = factory;
            using var __ = client;

            var userA = Guid.NewGuid();
            var userB = Guid.NewGuid();

            var tokenA = factory.CreateHumanToken(userA, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
            var tokenB = factory.CreateHumanToken(userB, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);

            // 1. User A GET /me when not created -> 404
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
            var getNotFound = await client.GetAsync("api/v1/inventory/warehouse/me");
            Assert.Equal(HttpStatusCode.NotFound, getNotFound.StatusCode);

            // 2. User A PUT /me with invalid location pair -> 400
            var invalidLocReq = new UpdateWarehouseRequest("123 Le Loi", "79", "00070", 21.0, 105.8);
            var putInvalidLoc = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me", invalidLocReq);
            Assert.Equal(HttpStatusCode.BadRequest, putInvalidLoc.StatusCode);

            // 3. User A PUT /me with valid location -> 200 Created/Configured
            var validReq = new UpdateWarehouseRequest("Số 1 Tràng Tiền", "01", "00070", 21.025, 105.856);
            var putValid = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me", validReq);
            Assert.Equal(HttpStatusCode.OK, putValid.StatusCode);
            var whA = await putValid.Content.ReadFromJsonAsync<WarehouseResponse>();
            Assert.NotNull(whA);
            Assert.Equal(userA, whA.OwnerUserId);
            Assert.Equal("Số 1 Tràng Tiền", whA.AddressDetail);
            Assert.Equal("01", whA.ProvinceCode);
            Assert.Equal("Thành phố Hà Nội", whA.ProvinceName);
            Assert.Equal("00070", whA.WardCode);
            Assert.Equal("Phường Hoàn Kiếm", whA.WardName);
            Assert.True(whA.HasValidAddress);

            // 4. User A GET /me -> returns configured address
            var getA = await client.GetAsync("api/v1/inventory/warehouse/me");
            Assert.Equal(HttpStatusCode.OK, getA.StatusCode);
            var gotA = await getA.Content.ReadFromJsonAsync<WarehouseResponse>();
            Assert.NotNull(gotA);
            Assert.Equal("Số 1 Tràng Tiền", gotA.AddressDetail);

            // 5. User B GET /me -> 404 (isolation: user B does NOT see User A's warehouse)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
            var getB = await client.GetAsync("api/v1/inventory/warehouse/me");
            Assert.Equal(HttpStatusCode.NotFound, getB.StatusCode);

            // 6. User A update with expectedVersion mismatch -> 409 Conflict
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
            var conflictReq = new UpdateWarehouseRequest("Số 2 Tràng Tiền", "01", "00070", 21.025, 105.856, ExpectedVersion: whA.Version + 99);
            var putConflict = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me", conflictReq);
            Assert.Equal(HttpStatusCode.Conflict, putConflict.StatusCode);

            // 7. User A update with correct expectedVersion -> 200 OK
            var correctReq = new UpdateWarehouseRequest("Số 2 Tràng Tiền", "01", "00070", 21.025, 105.856, ExpectedVersion: whA.Version);
            var putSuccess = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me", correctReq);
            Assert.Equal(HttpStatusCode.OK, putSuccess.StatusCode);
            var updatedA = await putSuccess.Content.ReadFromJsonAsync<WarehouseResponse>();
            Assert.NotNull(updatedA);
            Assert.Equal("Số 2 Tràng Tiền", updatedA.AddressDetail);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Warehouse_Put_Authorization_PermissionAndAdminRules()
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            var (factory, client) = CreateClient(fixture);
            await using var _ = factory;
            using var __ = client;

            var userWithoutPerm = Guid.NewGuid();
            var adminUser = Guid.NewGuid();

            // 1. Human without inventory.asset.manage permission -> 403 Forbidden
            var tokenWithoutPerm = factory.CreateHumanToken(userWithoutPerm, permissions: []);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenWithoutPerm);
            var putNoPerm = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me",
                new UpdateWarehouseRequest("123 Le Loi", "01", "00070", 21.0, 105.8));
            Assert.Equal(HttpStatusCode.Forbidden, putNoPerm.StatusCode);

            // 2. Human Admin without explicit permission -> 200 OK (operates on own warehouse via JWT sub)
            var adminToken = factory.CreateAdminToken(adminUser);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var putAdmin = await client.PutAsJsonAsync("api/v1/inventory/warehouse/me",
                new UpdateWarehouseRequest("Số 1 Tràng Tiền", "01", "00070", 21.025, 105.856));
            Assert.Equal(HttpStatusCode.OK, putAdmin.StatusCode);
            var adminWh = await putAdmin.Content.ReadFromJsonAsync<WarehouseResponse>();
            Assert.NotNull(adminWh);
            Assert.Equal(adminUser, adminWh.OwnerUserId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"longitude\":105.8}")] // missing latitude
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"latitude\":21.0}")] // missing longitude
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\"}")] // missing both
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"latitude\":null,\"longitude\":105.8}")] // null latitude
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"latitude\":21.0,\"longitude\":null}")] // null longitude
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"latitude\":\"not-a-number\",\"longitude\":105.8}")] // invalid type
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"latitude\":95.0,\"longitude\":105.8}")] // out of range lat
    [InlineData("{\"addressDetail\":\"123 Le Loi\",\"provinceCode\":\"01\",\"wardCode\":\"00070\",\"latitude\":21.0,\"longitude\":200.0}")] // out of range lng
    public async Task Warehouse_Put_CoordinateValidation_RejectsMissingNullAndInvalid(string jsonPayload)
    {
        var fixture = new PostgresFixture();
        await fixture.InitializeAsync();

        try
        {
            var (factory, client) = CreateClient(fixture);
            await using var _ = factory;
            using var __ = client;

            var user = Guid.NewGuid();
            var token = factory.CreateHumanToken(user, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PutAsync("api/v1/inventory/warehouse/me", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            // Verify database was NOT changed
            var options = new DbContextOptionsBuilder<Dprental.InventoryService.Domain.Repositories.InventoryServiceDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.MigrationsHistoryTable(
                    Dprental.InventoryService.Domain.Repositories.InventoryServiceDbContext.MigrationsHistoryTableName,
                    Dprental.InventoryService.Domain.Repositories.InventoryServiceDbContext.SchemaName))
                .Options;
            await using var db = new Dprental.InventoryService.Domain.Repositories.InventoryServiceDbContext(options);
            Assert.False(await db.Warehouses.AnyAsync(w => w.OwnerUserId == user));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
