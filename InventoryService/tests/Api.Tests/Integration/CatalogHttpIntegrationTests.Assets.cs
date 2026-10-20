using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Listings;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class CatalogHttpIntegrationTests
{
    [Fact]
    public async Task CreateAsset_EndToEnd_RealHttpClient_Success201_VerifiesUriBodyAndPersists()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var callerToken = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (req.HttpMethod == "GET" && path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-FX3",
                    name = "Sony FX3 Cinema Line",
                    status = "Published",
                    createdAt = "2026-09-01T00:00:00Z"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (req.HttpMethod == "GET" && path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-SONY",
                    name = "Sony",
                    slug = "sony",
                    logoMediaId = (Guid?)null,
                    logoUrl = (string?)null,
                    isActive = true
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (req.HttpMethod == "POST" && path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    valid = true,
                    errors = Array.Empty<object>()
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404, "Not Found"));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", callerToken);

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3 Cinema Line",
            model = "Sony FX3 Cinema Line",
            serialNumber = "FX3-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame", resolution = "4K" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" },
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(created);
        Assert.Equal(brandId, created.BrandId);
        Assert.Equal(eqTypeId, created.EquipmentTypeId);
        Assert.Equal("Sony FX3 Cinema Line", created.Model);
        lock (server.CapturedRequests)
        {
            Assert.Equal(3, server.CapturedRequests.Count);

            var req1 = server.CapturedRequests[0];
            Assert.Equal("GET", req1.Method);
            Assert.Equal($"/api/v1/catalog/equipment-types/{eqTypeId:D}", req1.PathAndQuery);

            var req2 = server.CapturedRequests[1];
            Assert.Equal("GET", req2.Method);
            Assert.Equal($"/api/v1/catalog/brands/{brandId:D}", req2.PathAndQuery);

            var req3 = server.CapturedRequests[2];
            Assert.Equal("POST", req3.Method);
            Assert.Equal($"/api/v1/catalog/equipment-types/{eqTypeId:D}/specifications/validate", req3.PathAndQuery);
            Assert.Equal($"Bearer {callerToken}", req3.Authorization);

            using var specDoc = JsonDocument.Parse(req3.Body);
            Assert.Equal(1, specDoc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Full-Frame", specDoc.RootElement.GetProperty("specValues").GetProperty("sensor").GetString());
            Assert.Equal("4K", specDoc.RootElement.GetProperty("specValues").GetProperty("resolution").GetString());
        }

        await using var db = CreateDbContext();
        var dbListing = await db.EquipmentListings.SingleAsync(l => l.Id == created.Id);
        Assert.Equal(brandId, dbListing.BrandId);
        Assert.Equal(1, dbListing.SchemaVersion);
        Assert.Contains("\"Full-Frame\"", dbListing.SpecValues);
    }

    [Fact]
    public async Task PatchAsset_EndToEnd_RealHttpClient_Success200_VerifiesUriBodyAndPersists()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var oldBrandId = Guid.NewGuid();
        var newBrandId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var callerToken = secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, eqTypeId: eqTypeId, brandId: oldBrandId, model: "Old Camera", schemaVersion: 1, specValues: "{\"lens\":\"24-70\"}", now: FixedNow);
        }

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (req.HttpMethod == "GET" && path.Contains($"brands/{newBrandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = newBrandId,
                    code = "BR-CANON",
                    name = "Canon",
                    slug = "canon",
                    isActive = true
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (req.HttpMethod == "POST" && path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    valid = true,
                    errors = Array.Empty<object>()
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404, "Not Found"));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", callerToken);

        var patch = new
        {
            brandId = newBrandId,
            schemaVersion = 2,
            specValues = new { lens = "70-200" }
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<EquipmentListingResponse>();
        Assert.NotNull(updated);
        Assert.Equal(newBrandId, updated.BrandId);
        Assert.Equal(2, updated.SchemaVersion);
        lock (server.CapturedRequests)
        {
            Assert.Equal(2, server.CapturedRequests.Count);
            var specReq = server.CapturedRequests.First(r => r.Method == "POST");
            Assert.Equal($"/api/v1/catalog/equipment-types/{eqTypeId:D}/specifications/validate", specReq.PathAndQuery);
            Assert.Equal($"Bearer {callerToken}", specReq.Authorization);
        }

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == assetId);
        Assert.Equal(newBrandId, reloaded.BrandId);
        Assert.Equal(2, reloaded.SchemaVersion);
        Assert.Contains("70-200", reloaded.SpecValues);
    }

    [Fact]
    public async Task CreateAsset_SpecificationValidationFailure_Returns400_WithErrorsExtension_DoesNotWriteAsset_DoesNotCallMedia()
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
                var json = JsonSerializer.Serialize(new
                {
                    valid = false,
                    errors = new[]
                    {
                        new { field = "sensor", code = "REQUIRED", message = "Trường này là bắt buộc." }
                    }
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var thumbId = Guid.NewGuid();
        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" },
            mediaIds = new[] { thumbId },
            thumbnailMediaId = thumbId
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("SPEC_VALIDATION_FAILED", problem.Title);
        Assert.True(problem.Extensions.ContainsKey("errors"));

        var errorsJson = ((JsonElement)problem.Extensions["errors"]!).GetRawText();
        Assert.Contains("\"field\":\"sensor\"", errorsJson);
        Assert.Contains("\"code\":\"REQUIRED\"", errorsJson);
        Assert.Contains("\"message\":\"Trường này là bắt buộc.\"", errorsJson);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Theory]
    [InlineData("sensor", "REQUIRED", "Trường này là bắt buộc.")]
    [InlineData("frameRate", "TYPE_MISMATCH", "Kiểu dữ liệu không hợp lệ.")]
    [InlineData("frameRate", "OUT_OF_RANGE", "Giá trị nằm ngoài phạm vi cho phép.")]
    [InlineData("mount", "INVALID_ENUM_OPTION", "Giá trị không thuộc danh sách tùy chọn hợp lệ.")]
    [InlineData("legacyPort", "UNKNOWN_FIELD", "Trường này không tồn tại trong schema.")]
    public async Task CreateAsset_FiveSpecificationErrorCodes_Returns400_PreservesErrorsListAndDoesNotWriteAsset(
        string field,
        string code,
        string message)
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
                var json = JsonSerializer.Serialize(new
                {
                    valid = false,
                    errors = new[]
                    {
                        new { field, code, message }
                    }
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"equipment-types/{eqTypeId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-01",
                    name = "Type",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                });
                return Task.FromResult(new MockHttpResponse(200, json));
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
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("SPEC_VALIDATION_FAILED", problem.Title);

        Assert.True(problem.Extensions.ContainsKey("errors"));
        var errorsJson = ((JsonElement)problem.Extensions["errors"]!).GetRawText();
        Assert.Contains($"\"field\":\"{field}\"", errorsJson);
        Assert.Contains($"\"code\":\"{code}\"", errorsJson);
        Assert.Contains($"\"message\":\"{message}\"", errorsJson);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_MultipleErrors_PreservesExactOrderAndElements()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
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
                var json = JsonSerializer.Serialize(new
                {
                    valid = false,
                    errors = new[]
                    {
                        new { field = "sensor", code = "REQUIRED", message = "Trường này là bắt buộc." },
                        new { field = "resolution", code = "TYPE_MISMATCH", message = "Kiểu dữ liệu không hợp lệ." },
                        new { field = "extraKey", code = "UNKNOWN_FIELD", message = "Trường này không tồn tại trong schema." }
                    }
                });
                return Task.FromResult(new MockHttpResponse(200, json));
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
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { resolution = 123 },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("SPEC_VALIDATION_FAILED", problem.Title);

        var errorsElement = (JsonElement)problem.Extensions["errors"]!;
        Assert.Equal(3, errorsElement.GetArrayLength());
        Assert.Equal("sensor", errorsElement[0].GetProperty("field").GetString());
        Assert.Equal("resolution", errorsElement[1].GetProperty("field").GetString());
        Assert.Equal("extraKey", errorsElement[2].GetProperty("field").GetString());
    }

    [Fact]
    public async Task PatchAsset_InvalidSpec_Returns400_DoesNotModifyStoredSnapshot()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, eqTypeId: eqTypeId, brandId: brandId, model: "Model Original", schemaVersion: 1, specValues: "{\"lens\":\"24-70\"}", now: FixedNow);
        }

        server.Handler = req =>
        {
            var path = req.RawUrl ?? string.Empty;
            if (path.Contains($"equipment-types/{eqTypeId:D}/specifications/validate"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    valid = false,
                    errors = new[]
                    {
                        new { field = "lens", code = "OUT_OF_RANGE", message = "Giá trị ngoài phạm vi." }
                    }
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            specValues = new { lens = "invalid-range" }
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("SPEC_VALIDATION_FAILED", problem.Title);

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == assetId);
        Assert.Equal(1, reloaded.SchemaVersion);
        Assert.Contains("24-70", reloaded.SpecValues);
    }

    [Fact]
    public async Task CreateAsset_BrandMissing404_Returns400_DoesNotWriteAsset_DoesNotCallMedia()
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
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-FX3",
                    name = "Sony FX3",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var problem = JsonSerializer.Serialize(new
                {
                    title = "Brand.NotFound",
                    detail = "Thương hiệu không tồn tại trong Catalog.",
                    status = 404
                });
                return Task.FromResult(new MockHttpResponse(404, problem, "application/problem+json"));
            }

            return Task.FromResult(new MockHttpResponse(404, "Not Found"));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var thumbId = Guid.NewGuid();
        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" },
            mediaIds = new[] { thumbId },
            thumbnailMediaId = thumbId
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problemResp = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problemResp);
        Assert.Equal("Brand.NotFound", problemResp.Title);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_BrandInactive200_Returns400_DoesNotWriteAsset_DoesNotCallMedia()
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
                var json = JsonSerializer.Serialize(new
                {
                    id = eqTypeId,
                    code = "EQ-FX3",
                    name = "Sony FX3",
                    status = "Published"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-OLD",
                    name = "Old Brand",
                    slug = "old-brand",
                    isActive = false
                });
                return Task.FromResult(new MockHttpResponse(200, json));
            }

            return Task.FromResult(new MockHttpResponse(404));
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var thumbId = Guid.NewGuid();
        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            name = "Sony FX3",
            model = "Sony FX3",
            serialNumber = "FX3-SN-001",
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" },
            mediaIds = new[] { thumbId },
            thumbnailMediaId = thumbId
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problemResp = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problemResp);
        Assert.Equal("Brand.Inactive", problemResp.Title);

        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_BrandMissingIsActiveProperty200_Returns503()
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
                    code = "EQ-FX3",
                    name = "Sony FX3",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                // Missing isActive property
                var json = JsonSerializer.Serialize(new
                {
                    id = brandId,
                    code = "BR-01",
                    name = "Brand"
                });
                return Task.FromResult(new MockHttpResponse(200, json));
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
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(0, mediaClient.VerifyCalls);

        await using var db = CreateDbContext();
        Assert.Null(await db.EquipmentListings.SingleOrDefaultAsync(l => l.OwnerUserId == ownerId));
    }

    [Fact]
    public async Task CreateAsset_BrandIdMismatch200_Returns503()
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
                    code = "EQ-FX3",
                    name = "Sony FX3",
                    status = "Published"
                })));
            }

            if (path.Contains($"brands/{brandId:D}"))
            {
                // Returned id does not match requested id
                var json = JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid(),
                    code = "BR-01",
                    name = "Brand",
                    isActive = true
                });
                return Task.FromResult(new MockHttpResponse(200, json));
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
            manufactureYear = 2024,
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
    public async Task CreateAsset_SchemaNotFound404_Returns400Validation()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
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
                var problem = JsonSerializer.Serialize(new
                {
                    title = "EquipmentType.SchemaNotFound",
                    detail = "Schema version 3 chưa được publish cho loại thiết bị.",
                    status = 404
                });
                return Task.FromResult(new MockHttpResponse(404, problem, "application/problem+json"));
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
            manufactureYear = 2024,
            schemaVersion = 3,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("EquipmentType.SchemaNotFound", problem.Title);
        Assert.Equal("Schema version 3 chưa được publish cho loại thiết bị.", problem.Detail);
    }

    [Fact]
    public async Task CreateAsset_CatalogInvalidRequest400_Returns400Validation()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
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
                var problem = JsonSerializer.Serialize(new
                {
                    title = "Catalog.InvalidRequest",
                    detail = "Request validation không hợp lệ.",
                    status = 400
                });
                return Task.FromResult(new MockHttpResponse(400, problem, "application/problem+json"));
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
            manufactureYear = 2024,
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" },
            rentalRate = new { dailyRate = 100_000m, currency = "VND" }
        };

        var response = await client.PostAsync("api/v1/inventory/equipment-listings",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Catalog.InvalidRequest", problem.Title);
        Assert.Equal("Request validation không hợp lệ.", problem.Detail);
    }

    [Fact]
    public async Task PatchAsset_ModelOnly_DoesNotSendCatalogRequests()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, brandId: brandId, model: "Model Before", schemaVersion: 1, specValues: "{}", now: FixedNow);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            model = "Model After"
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        lock (server.CapturedRequests)
        {
            Assert.Empty(server.CapturedRequests);
        }

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == assetId);
        Assert.Equal("Model After", reloaded.Model);
    }

    [Fact]
    public async Task PatchAsset_AssetVersion1_RemainsVersion1_WhenCatalogHasVersion2()
    {
        await using var server = new LocalCatalogServer();
        var (app, secFactory, _) = CreateFactory(server.BaseUrl);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var ownerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();

        await using (var db = CreateDbContext())
        {
            await IntegrationTestHelper.InsertTestListingAsync(db, listingId: assetId, ownerId: ownerId, eqTypeId: eqTypeId, brandId: brandId, model: "Model V1", schemaVersion: 1, specValues: "{\"field\":\"value\"}", now: FixedNow);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(ownerId, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patch = new
        {
            model = "Model V1 Renamed"
        };

        var response = await client.PatchAsync($"api/v1/inventory/equipment-listings/{assetId:D}",
            new StringContent(JsonSerializer.Serialize(patch), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        lock (server.CapturedRequests)
        {
            Assert.Empty(server.CapturedRequests);
        }

        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.EquipmentListings.SingleAsync(l => l.Id == assetId);
        Assert.Equal(1, reloaded.SchemaVersion);
        Assert.Equal("Model V1 Renamed", reloaded.Model);
    }
}
