using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using System.Text.Json;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task Patch_PhysicalAndDynamicFields_ExplicitNullClearsFields()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Old Model", serial: "SN-OLD", year: 2022);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // Explicit null for serialNumber and manufactureYear to clear them
        var patchJson = """
        {
            "model": "Updated Model",
            "serialNumber": null,
            "manufactureYear": null
        }
        """;

        var content = new StringContent(patchJson, Encoding.UTF8, "application/json");
        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Updated Model", updated.Model);
        Assert.Null(updated.SerialNumber);
        Assert.Null(updated.ManufactureYear);

        // Verify in PostgreSQL
        await using var db = CreateDbContext();
        var reloaded = await db.Assets.SingleAsync(a => a.Id == asset.Id);
        Assert.Equal("Updated Model", reloaded.Model);
        Assert.Null(reloaded.SerialNumber);
        Assert.Null(reloaded.ManufactureYear);
    }

    [Fact]
    public async Task ToggleAcceptingBookings_FalseTrue_PreservesAllocationsAndEmitsOutboxAtomically()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Booking Test Asset");

        // Seed an allocation and calendar block on the asset
        await using (var db = CreateDbContext())
        {
            var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
            var alloc = AssetAllocation.CreateManual(Guid.NewGuid(), asset.Id, period, FixedNow).Value;
            var block = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 15),
                CalendarBlockReason.Maintenance, owner.ToString("D"), FixedNow, new DateOnly(2026, 10, 2)).Value;

            db.AssetAllocations.Add(alloc);
            db.CalendarBlocks.Add(block);
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // 1. Toggle false
        var disableJson = """
        {
            "acceptingBookings": false,
            "reason": "Tam dung de bao duong dinh ky"
        }
        """;
        var disableResp = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}/accepting-bookings",
            new StringContent(disableJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);
        var disabled = await disableResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(disabled);
        Assert.False(disabled.AcceptingBookings);
        Assert.Equal("Tam dung de bao duong dinh ky", disabled.SuspensionReason);

        // Verify allocation and calendar block are untouched in PostgreSQL
        await using (var db = CreateDbContext())
        {
            Assert.Single(await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync());
            Assert.Single(await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync());

            // Check outbox message emitted atomically
            var outboxMessages = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Single(outboxMessages);
            Assert.Equal("inventory.asset.events.v1", outboxMessages[0].Topic);
            Assert.Contains("InventoryAssetAvailabilityChanged", outboxMessages[0].Payload);
        }

        // 2. Toggle true -> clears reason and emits second outbox event
        var enableJson = """
        {
            "acceptingBookings": true
        }
        """;
        var enableResp = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}/accepting-bookings",
            new StringContent(enableJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);
        var enabled = await enableResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(enabled);
        Assert.True(enabled.AcceptingBookings);
        Assert.Null(enabled.SuspensionReason);

        await using (var db = CreateDbContext())
        {
            var outboxMessages = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Equal(2, outboxMessages.Count);
        }
    }

    [Fact]
    public async Task Patch_RaceAfterSnapshot_Returns409_AndDoesNotCorruptAsset()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Initial Model");

        bool raceTriggered = false;
        var factory = new InventorySecurityTestFactory(_fixture.ConnectionString, new TestTimeProvider(FixedNow));
        await using (factory)
        {
            await using var app = factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ICatalogServiceClient>();
                    services.AddSingleton<ICatalogServiceClient>(new StubCatalogClient());

                    services.RemoveAll<IMediaServiceClient>();
                    services.AddSingleton<IMediaServiceClient>(new StubMediaClient());

                    var originalDescriptor = services.First(d => d.ServiceType == typeof(IInventoryAssetRepository));
                    services.Remove(originalDescriptor);
                    services.AddScoped<IInventoryAssetRepository>(provider =>
                    {
                        var db = provider.GetRequiredService<InventoryServiceDbContext>();
                        var inner = new InventoryAssetRepository(db);
                        return new RaceInterceptingAssetRepository(inner, () =>
                        {
                            if (!raceTriggered)
                            {
                                raceTriggered = true;
                                using var npgsql = new NpgsqlConnection(_fixture.ConnectionString);
                                npgsql.Open();
                                using var cmd = new NpgsqlCommand($"UPDATE inventory.assets SET model = 'Concurrent Model', updated_at = NOW() WHERE id = '{asset.Id:D}';", npgsql);
                                cmd.ExecuteNonQuery();
                            }
                        });
                    });
                });
            });

            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                factory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

            var patchJson = """
            {
                "model": "Stale Overwrite Attempt"
            }
            """;

            var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
                new StringContent(patchJson, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        // Verify in DB that asset was not corrupted or overwritten
        await using (var db = CreateDbContext())
        {
            var currentModel = (await db.Assets.SingleAsync(a => a.Id == asset.Id)).Model;
            Assert.Equal("Concurrent Model", currentModel);
        }
    }

    [Fact]
    public async Task CommitFailure_RollsBackAssetAndOutboxAtomically()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Rollback Test Asset");

        // Install failing trigger on outbox_messages in PostgreSQL
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        string triggerFuncName = $"trg_fail_outbox_asset_{asset.Id:N}";
        string triggerName = $"trg_check_asset_{asset.Id:N}";

        try
        {
            string createTriggerSql = $@"
                CREATE OR REPLACE FUNCTION {triggerFuncName}()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.kafka_key = '{asset.Id:D}' THEN
                        RAISE EXCEPTION 'Simulated atomic rollback failure for asset %', '{asset.Id:D}';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON inventory.outbox_messages
                FOR EACH ROW
                EXECUTE FUNCTION {triggerFuncName}();
            ";

            await using (var cmd = new NpgsqlCommand(createTriggerSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

            var disableJson = """
            {
                "acceptingBookings": false,
                "reason": "This toggle should rollback"
            }
            """;

            var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}/accepting-bookings",
                new StringContent(disableJson, Encoding.UTF8, "application/json"));

            // Must fail with 500 and title TRANSACTION_FAILED due to transaction abort
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
            Assert.NotNull(problem);
            Assert.Equal("TRANSACTION_FAILED", problem.Title);

            // Asset MUST NOT have changed in PostgreSQL!
            await using var verifyDb = CreateDbContext();
            var reloaded = await verifyDb.Assets.SingleAsync(a => a.Id == asset.Id);
            Assert.True(reloaded.AcceptingBookings);
            Assert.Null(reloaded.SuspensionReason);

            var outboxList = await verifyDb.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Empty(outboxList);
        }
        finally
        {
            string dropTriggerSql = $@"
                DROP TRIGGER IF EXISTS {triggerName} ON inventory.outbox_messages;
                DROP FUNCTION IF EXISTS {triggerFuncName}();
            ";
            await using var dropCmd = new NpgsqlCommand(dropTriggerSql, connection);
            await dropCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Post_BrandNotFound_Returns400ProblemDetails()
    {
        var stubCatalog = new StubCatalogClient
        {
            BrandHandler = _ => Task.FromResult(Result.Failure<CatalogBrandResponse>(
                Error.Validation("Brand.NotFound", "Brand không tồn tại trong Catalog.")))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Brand.NotFound", body);

        // Verify no asset written
        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == owner));
    }

    [Fact]
    public async Task Post_BrandInactive_Returns400ProblemDetails()
    {
        var stubCatalog = new StubCatalogClient
        {
            BrandHandler = id => Task.FromResult(Result.Success(
                new CatalogBrandResponse(id, "BR-OLD", "Old Brand", "Inactive")))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Brand.Inactive", body);
    }

    [Theory]
    [InlineData("{\"equipmentTypeId\":\"" + "11111111-1111-1111-1111-111111111111" + "\",\"model\":\"Sony FX3\",\"schemaVersion\":1,\"specValues\":{}}")]
    [InlineData("{\"equipmentTypeId\":\"" + "11111111-1111-1111-1111-111111111111" + "\",\"brandId\":null,\"model\":\"Sony FX3\",\"schemaVersion\":1,\"specValues\":{}}")]
    [InlineData("{\"equipmentTypeId\":\"" + "11111111-1111-1111-1111-111111111111" + "\",\"brandId\":\"00000000-0000-0000-0000-000000000000\",\"model\":\"Sony FX3\",\"schemaVersion\":1,\"specValues\":{}}")]
    public async Task Post_MissingOrNullOrEmptyBrandId_Returns400(string payloadJson)
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(payloadJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_CatalogSpecValidationFailure_PreservesContractCodeAndDetail_AndDoesNotWriteAsset()
    {
        var stubCatalog = new StubCatalogClient
        {
            SpecHandler = (_, _, _) => Task.FromResult(Result.Failure(
                Error.Validation("EquipmentType.SpecRequired", "Trường 'resolution' là bắt buộc.")))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { sensor = "Full-Frame" }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("EquipmentType.SpecRequired", problem.Title);
        Assert.Equal("Trường 'resolution' là bắt buộc.", problem.Detail);

        // Database has NO record written
        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == owner));
    }

    [Fact]
    public async Task Patch_Brand_Success200_UpdatesBrandInDatabase()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var oldBrand = Guid.NewGuid();
        var newBrand = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, brandId: oldBrand);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patchJson = $"{{\"brandId\":\"{newBrand}\"}}";
        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(updated);
        Assert.Equal(newBrand, updated.BrandId);

        // Verify in PostgreSQL
        await using var db = CreateDbContext();
        var reloaded = await db.Assets.SingleAsync(a => a.Id == asset.Id);
        Assert.Equal(newBrand, reloaded.BrandId);
    }

    [Fact]
    public async Task Patch_BrandInactive_Returns400ProblemDetails()
    {
        var inactiveBrand = Guid.NewGuid();
        var stubCatalog = new StubCatalogClient
        {
            BrandHandler = id => Task.FromResult(Result.Success(
                new CatalogBrandResponse(id, "BR-INACTIVE", "Inactive Brand", "Inactive")))
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patchJson = $"{{\"brandId\":\"{inactiveBrand}\"}}";
        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Brand.Inactive", body);
    }

    [Fact]
    public async Task Patch_CannotRemoveBrand_Returns400()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // Sending null for brandId is rejected by converter with 400
        var patchJson = "{\"brandId\":null}";
        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Patch_ModelOnly_DoesNotCallCatalog()
    {
        bool catalogCalled = false;
        var stubCatalog = new StubCatalogClient
        {
            LookupHandler = _ => { catalogCalled = true; return Task.FromResult(Result.Success(new CatalogEquipmentTypeResponse(Guid.NewGuid(), "C", "N", null, 1, DateTime.UtcNow, null, null))); },
            BrandHandler = _ => { catalogCalled = true; return Task.FromResult(Result.Success(new CatalogBrandResponse(Guid.NewGuid(), "C", "N", "Active"))); },
            SpecHandler = (_, _, _) => { catalogCalled = true; return Task.FromResult(Result.Success()); }
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Model Before");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var patchJson = "{\"model\":\"Model After\"}";
        var response = await client.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(catalogCalled);

        await using var db = CreateDbContext();
        var reloaded = await db.Assets.SingleAsync(a => a.Id == asset.Id);
        Assert.Equal("Model After", reloaded.Model);
    }

    [Fact]
    public async Task Patch_OldAssetNullBrand_PreservesNullWhenOmitted_AndUpdatesWhenSent()
    {
        var (app, secFactory) = CreateFactory();
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        // Insert directly in PostgreSQL an asset with brand_id = NULL (simulating pre-migration asset)
        await using (var db = CreateDbContext())
        {
            var eqId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO inventory.assets (id, owner_user_id, equipment_type_id, brand_id, model, schema_version, spec_values, accepting_bookings, created_at, updated_at)
                VALUES ({{assetId}}, {{owner}}, {{eqId}}, NULL, 'Legacy Camera', 1, '{"res":"1080p"}'::jsonb, true, {{FixedNow}}, {{FixedNow}});
            """);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // 1. PATCH model only -> preserves brand_id = null
        var patch1 = "{\"model\":\"Legacy Camera Renamed\"}";
        var resp1 = await client.PatchAsync($"api/v1/inventory/assets/{assetId:D}",
            new StringContent(patch1, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var assetDto1 = await resp1.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(assetDto1);
        Assert.Null(assetDto1.BrandId);

        // 2. PATCH with valid brand -> backfills brand_id
        var newBrand = Guid.NewGuid();
        var patch2 = $"{{\"brandId\":\"{newBrand}\"}}";
        var resp2 = await client.PatchAsync($"api/v1/inventory/assets/{assetId:D}",
            new StringContent(patch2, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var assetDto2 = await resp2.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(assetDto2);
        Assert.Equal(newBrand, assetDto2.BrandId);

        // Verify in PostgreSQL
        await using (var db = CreateDbContext())
        {
            var reloaded = await db.Assets.SingleAsync(a => a.Id == assetId);
            Assert.Equal(newBrand, reloaded.BrandId);
        }
    }

    [Fact]
    public async Task Post_TypeAWithoutVersion_TypeBWithVersion_TypeAStillRejectedWith400()
    {
        var typeA = Guid.NewGuid();
        var typeB = Guid.NewGuid();
        const int version2 = 2;

        var stubCatalog = new StubCatalogClient
        {
            SpecHandler = (eqId, ver, _) =>
            {
                if (eqId == typeA && ver == version2)
                {
                    return Task.FromResult(Result.Failure(
                        Error.Validation("EquipmentType.InvalidAssetSchemaVersion", "Schema version 2 chưa được publish cho loại thiết bị Type A.")));
                }
                if (eqId == typeB && ver == version2)
                {
                    return Task.FromResult(Result.Success());
                }
                return Task.FromResult(Result.Success());
            }
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // Post for Type A with version 2
        var payloadA = new
        {
            equipmentTypeId = typeA,
            brandId = Guid.NewGuid(),
            model = "Camera A",
            schemaVersion = version2,
            specValues = new { res = "4K" }
        };

        var responseA = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payloadA), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, responseA.StatusCode);
        var problemA = await responseA.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problemA);
        Assert.Equal("EquipmentType.InvalidAssetSchemaVersion", problemA.Title);
        Assert.Equal("Schema version 2 chưa được publish cho loại thiết bị Type A.", problemA.Detail);
    }

    [Fact]
    public async Task Patch_SpecOnDisabledEquipmentType_UsingPublishedOldSchema_Succeeds()
    {
        var disabledEquipmentTypeId = Guid.NewGuid();
        const int oldPublishedVersion = 1;

        // Catalog specification validation validates against the old published schema (even if equipment type is now Disabled)
        var stubCatalog = new StubCatalogClient
        {
            SpecHandler = (eqId, ver, _) =>
            {
                Assert.Equal(disabledEquipmentTypeId, eqId);
                Assert.Equal(oldPublishedVersion, ver);
                return Task.FromResult(Result.Success());
            }
        };

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        await using var _ = app;
        await using var __ = secFactory;
        using var client = app.CreateClient();

        var owner = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Seed existing asset with Disabled equipment type and old published schema version 1
        await using (var db = CreateDbContext())
        {
            var asset = InventoryAsset.Create(
                assetId, owner, disabledEquipmentTypeId, brandId,
                "Legacy Camera", oldPublishedVersion, "{\"codec\":\"H264\"}", FixedNow).Value;
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // Patch spec values (uses existing schemaVersion = 1, does NOT re-query equipment type published status)
        var patchJson = "{\"specValues\":{\"codec\":\"ProRes\"}}";
        var response = await client.PatchAsync($"api/v1/inventory/assets/{assetId:D}",
            new StringContent(patchJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(updated);
        Assert.Equal(oldPublishedVersion, updated.SchemaVersion);

        // Verify persisted in PostgreSQL
        await using var verifyDb = CreateDbContext();
        var reloaded = await verifyDb.Assets.SingleAsync(a => a.Id == assetId);
        Assert.Contains("ProRes", reloaded.SpecValues);
    }
}
