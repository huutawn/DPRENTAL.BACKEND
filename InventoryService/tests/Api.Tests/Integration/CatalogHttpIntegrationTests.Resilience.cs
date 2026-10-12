using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class CatalogHttpIntegrationTests
{
    [Fact]
    public async Task CreateAsset_NotImplemented501_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                return Task.FromResult(new MockHttpResponse(501));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                })));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_NoContent204_Returns503_NoFallback_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                return Task.FromResult(new MockHttpResponse(204));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                })));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" },
            serialNumber = "FX3-SN-001",
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_EndpointMissing404_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                return Task.FromResult(new MockHttpResponse(404, "<html404>"));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                })));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_MalformedJson_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, "malformed-not-json-content"));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_ContradictoryResponse_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    valid = true,
                    errors = new[] { new { field = "sensor", code = "REQUIRED", message = "Err" } }
                })));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                })));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task CreateAsset_UpstreamErrorCodes_Returns503_DoesNotWriteAsset(int upstreamStatus)
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            return Task.FromResult(new MockHttpResponse(upstreamStatus));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_ClientCancellationMidFlightDuringSpecification_DoesNotWriteAssetAndDoesNotCallMedia()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        using var cts = new CancellationTokenSource();
        var specRequestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.Handler = async req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                specRequestReceived.TrySetResult(true);
                try
                {
                    await Task.Delay(5000, cts.Token);
                }
                catch (OperationCanceledException) { }

                return new MockHttpResponse(200, JsonSerializer.Serialize(new { valid = true, errors = Array.Empty<object>() }));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                }));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                }));
            }

            return new MockHttpResponse(404);
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" },
            thumbnailMediaId = Guid.NewGuid()
        };

        var postTask = client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cts.Token);

        // Chờ request validation thực sự tới CatalogServer trước khi cancel
        await specRequestReceived.Task;

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await postTask);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task PatchAsset_ClientCancellationMidFlightDuringSpecification_DoesNotModifyStoredSnapshot()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, eqTypeId: eqTypeId, brandId: brandId, model: "Model Before", schemaVersion: 1, specValues: "{\"lens\":\"24-70\"}", now: FixedNow);
        }

        using var cts = new CancellationTokenSource();
        var specRequestReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        server.Handler = async req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                specRequestReceived.TrySetResult(true);
                try
                {
                    await Task.Delay(5000, cts.Token);
                }
                catch (OperationCanceledException) { }

                return new MockHttpResponse(200, JsonSerializer.Serialize(new { valid = true, errors = Array.Empty<object>() }));
            }

            return new MockHttpResponse(404);
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            specValues = new { lens = "70-200" },
            thumbnailMediaId = Guid.NewGuid()
        };

        var patchTask = client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"),
            cts.Token);

        await specRequestReceived.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await patchTask);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == assetId);
        Assert.Equal("Model Before", reloaded.Model);
        Assert.Equal(1, reloaded.SchemaVersion);
        Assert.Contains("24-70", reloaded.SpecValues);
    }

    [Fact]
    public async Task CreateAsset_TimeoutAtSpecificationStep_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl, catalogTimeoutSeconds: 1);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = async req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                await Task.Delay(2500);
                return new MockHttpResponse(200, JsonSerializer.Serialize(new { valid = true, errors = Array.Empty<object>() }));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                }));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                }));
            }

            return new MockHttpResponse(404);
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CATALOG_SERVICE_TIMEOUT", body);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_TimeoutAtBrandStep_Returns503_DoesNotWriteAsset()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl, catalogTimeoutSeconds: 1);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = async req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                }));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                await Task.Delay(2500);
                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                }));
            }

            return new MockHttpResponse(404);
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("CATALOG_SERVICE_TIMEOUT", body);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_DependencyErrorAtBrandStep_Returns503_DoesNotCallSpecOrMedia()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(500));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        lock (server.CapturedRequests)
        {
            Assert.DoesNotContain(server.CapturedRequests, r => r.PathAndQuery.Contains("specifications/validate"));
        }

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task PatchAsset_DependencyErrorAtSpecificationStep_RetainsAllFieldsAndVersionUnmodified()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, mediaClient) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var initialBrandId = Guid.NewGuid();
        var initialThumbId = Guid.NewGuid();
        var newBrandId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, eqTypeId: eqTypeId, brandId: initialBrandId, model: "Model Before", schemaVersion: 1, specValues: "{\"lens\":\"24-70\"}", thumbnailMediaId: initialThumbId, now: FixedNow);
        }

        uint initialVersion;
        DateTimeOffset? initialUpdatedAt;
        string initialSpecs;
        await using (var db = CreateDbContext())
        {
            var original = await db.EquipmentListings.SingleAsync(l => l.Id == assetId);
            initialVersion = original.Version;
            initialUpdatedAt = original.UpdatedAt;
            initialSpecs = original.SpecValues;
        }

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"brands/{newBrandId:D}"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = newBrandId,
                    code = "BR-NEW",
                    name = "New Brand",
                    isActive = true
                })));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                return Task.FromResult(new MockHttpResponse(503));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            brandId = newBrandId,
            model = "Model New",
            schemaVersion = 2,
            specValues = new { lens = "70-200" },
            thumbnailMediaId = Guid.NewGuid()
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == assetId);
        Assert.Equal(initialBrandId, reloaded.BrandId);
        Assert.Equal("Model Before", reloaded.Model);
        Assert.Equal(1, reloaded.SchemaVersion);
        Assert.Equal(initialSpecs, reloaded.SpecValues);
        Assert.Equal(initialThumbId, reloaded.ThumbnailMediaId);
        Assert.Equal(initialUpdatedAt, reloaded.UpdatedAt);
        Assert.Equal(initialVersion, reloaded.Version);
    }

    [Fact]
    public async Task CreateAsset_ConcurrentRequests_JwtBelongsToCaller_NoLeakage()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var tokenA = secFactory.CreateHumanToken(userA, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        var tokenB = secFactory.CreateHumanToken(userB, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);

        var eqTypeIdA = Guid.NewGuid();
        var eqTypeIdB = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains("specifications/validate"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    valid = true,
                    errors = Array.Empty<object>()
                })));
            }

            if (path.Contains("equipment-types/"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = path.Contains(eqTypeIdA.ToString("D")) ? eqTypeIdA : eqTypeIdB,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                })));
            }

            if (path.Contains("brands/"))
            {
                return Task.FromResult(new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                })));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        using var clientA = app.CreateClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        using var clientB = app.CreateClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);

        var payloadA = new
        {
            equipmentTypeId = eqTypeIdA,
            brandId = brandId,
            name = "Camera A",
            model = "Camera A",
            serialNumber = "SN-CONCURRENT-A",
            schemaVersion = 1,
            specValues = new { key = "valA" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var payloadB = new
        {
            equipmentTypeId = eqTypeIdB,
            brandId = brandId,
            name = "Camera B",
            model = "Camera B",
            serialNumber = "SN-CONCURRENT-B",
            schemaVersion = 1,
            specValues = new { key = "valB" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var taskA = clientA.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payloadA), Encoding.UTF8, "application/json"));
        var taskB = clientB.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payloadB), Encoding.UTF8, "application/json"));
        var responses = await Task.WhenAll(taskA, taskB);
        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Created, responses[1].StatusCode);

        lock (server.CapturedRequests)
        {
            var specReqA = server.CapturedRequests.FirstOrDefault(r =>
                r.Method == "POST" && r.PathAndQuery.Contains(eqTypeIdA.ToString("D")));
            var specReqB = server.CapturedRequests.FirstOrDefault(r =>
                r.Method == "POST" && r.PathAndQuery.Contains(eqTypeIdB.ToString("D")));

            Assert.NotNull(specReqA);
            Assert.NotNull(specReqB);

            Assert.Equal($"Bearer {tokenA}", specReqA.Authorization);
            Assert.Equal($"Bearer {tokenB}", specReqB.Authorization);
        }
    }

    [Fact]
    public async Task CreateAsset_UnauthorizedCaller_BlockedBeforeDownstream()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            name = "Model",
            model = "Model",
            schemaVersion = 1,
            specValues = new { },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        lock (server.CapturedRequests)
        {
            Assert.Empty(server.CapturedRequests);
        }
    }

    [Fact]
    public async Task PatchAsset_NonOwnerCaller_BlockedBeforeDownstream()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, model: "Model", schemaVersion: 1, specValues: "{}", now: FixedNow);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(attackerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            model = "Hacked Model"
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        lock (server.CapturedRequests)
        {
            Assert.Empty(server.CapturedRequests);
        }
    }

    [Fact]
    public async Task PatchAsset_XminConflict_Returns409Conflict()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, eqTypeId: eqTypeId, brandId: brandId, model: "Model", schemaVersion: 1, specValues: "{}", now: FixedNow);
        }

        server.Handler = async req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains("specifications/validate"))
            {
                await using (var db = CreateDbContext())
                {
                    var listingToModify = await db.EquipmentListings.SingleAsync(l => l.Id == assetId);
                    listingToModify.UpdateDetails(
                        listingToModify.BrandId,
                        "Modified Name",
                        listingToModify.Description,
                        "Model Modified by Other",
                        listingToModify.ThumbnailMediaId,
                        listingToModify.SchemaVersion,
                        listingToModify.SpecValues,
                        FixedNow);
                    await db.SaveChangesAsync();
                }

                return new MockHttpResponse(200, JsonSerializer.Serialize(new
                {
                    valid = true,
                    errors = Array.Empty<object>()
                }));
            }

            return new MockHttpResponse(404);
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            specValues = new { updated = true }
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem.Title is "CONCURRENCY_CONFLICT" or "STALE_INVENTORY_WRITE");
    }
}
