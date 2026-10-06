using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    [Fact]
    public async Task CreateAsset_WithValidRentalRate_PersistsBothAssetAndRate()
    {
        var owner = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = eqTypeId,
            brandId = brandId,
            model = "Sony FX3 Cinema Line",
            schemaVersion = 1,
            specValues = new { resolution = "4K" },
            rentalRate = new
            {
                dailyRate = 500_000m,
                weeklyRate = 3_000_000m,
                monthlyRate = 10_000_000m,
                currency = "VND"
            }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(created);
        Assert.NotNull(created.RentalRate);
        Assert.Equal(500_000m, created.RentalRate.DailyRate);
        Assert.Equal(3_000_000m, created.RentalRate.WeeklyRate);
        Assert.Equal(10_000_000m, created.RentalRate.MonthlyRate);
        Assert.Equal("VND", created.RentalRate.Currency);

        // Verify persisted in PostgreSQL
        await using var db = CreateDbContext();
        var assetDb = await db.Assets.Include(a => a.RentalRate).FirstOrDefaultAsync(a => a.Id == created.Id);
        Assert.NotNull(assetDb);
        Assert.NotNull(assetDb.RentalRate);
        Assert.Equal(500_000m, assetDb.RentalRate.DailyRate);
        Assert.Equal(3_000_000m, assetDb.RentalRate.WeeklyRate);
        Assert.Equal(10_000_000m, assetDb.RentalRate.MonthlyRate);
        Assert.Equal("VND", assetDb.RentalRate.Currency);
    }

    [Fact]
    public async Task CreateAsset_MissingRentalRate_FailsBeforeCallingCatalogOrMedia()
    {
        var owner = Guid.NewGuid();
        int catalogCalls = 0;

        var stubCatalog = new StubCatalogClient
        {
            LookupHandler = _ =>
            {
                catalogCalls++;
                return Task.FromResult(Result.Success(new Dprental.InventoryService.Application.DTOs.Catalog.CatalogEquipmentTypeResponse(
                    Guid.NewGuid(), "EQ", "Name", null, 1, DateTime.UtcNow, null, null)));
            }
        };
        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { }
            // rentalRate is missing!
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("RENTAL_RATE_REQUIRED", problem.Title);
        Assert.Equal(0, catalogCalls); // Catalog was NOT invoked
    }

    [Theory]
    [InlineData(-100, "VND")]
    [InlineData(0, "VND")]
    [InlineData(500.5, "VND")]
    [InlineData(500000, "USD")]
    [InlineData(500000, "vnd")]
    public async Task CreateAsset_InvalidRentalRate_Returns400(decimal dailyRate, string currency)
    {
        var owner = Guid.NewGuid();
        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var payload = new
        {
            equipmentTypeId = Guid.NewGuid(),
            brandId = Guid.NewGuid(),
            model = "Sony FX3",
            schemaVersion = 1,
            specValues = new { },
            rentalRate = new
            {
                dailyRate,
                currency
            }
        };

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("0.99999999999999999999999999999999999")]
    [InlineData("1000.000000000000000000000000000000001")]
    [InlineData("1000.5")]
    [InlineData("1.55e1")]
    [InlineData("1e-1")]
    public async Task CreateAsset_RawJson_RoundingPrevention_RejectsFractionalNumbersEvenIfCloseToInteger(string rawDailyRate)
    {
        var owner = Guid.NewGuid();
        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        var rawJson = $$"""
        {
            "equipmentTypeId": "{{Guid.NewGuid():D}}",
            "brandId": "{{Guid.NewGuid():D}}",
            "model": "Sony FX3",
            "schemaVersion": 1,
            "specValues": {},
            "rentalRate": {
                "dailyRate": {{rawDailyRate}},
                "currency": "VND"
            }
        }
        """;

        var response = await client.PostAsync("api/v1/inventory/assets",
            new StringContent(rawJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify DB untouched
        await using var db = CreateDbContext();
        Assert.Null(await db.Assets.SingleOrDefaultAsync(a => a.OwnerUserId == owner));
    }

    [Theory]
    [InlineData("0.99999999999999999999999999999999999")]
    [InlineData("1000.000000000000000000000000000000001")]
    [InlineData("1000.5")]
    public async Task UpdateRentalRate_RawJson_RoundingPrevention_RejectsFractionalNumbers(string rawDailyRate)
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: []));

        var rawJson = $$"""
        {
            "dailyRate": {{rawDailyRate}},
            "currency": "VND"
        }
        """;

        var response = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(rawJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Verify original rate intact
        await using var db = CreateDbContext();
        var rate = await db.AssetRentalRates.FindAsync(asset.Id);
        Assert.NotNull(rate);
        Assert.Equal(100_000m, rate.DailyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_RawJson_AcceptsValidRepresentations_LikeScientificNotationAndTrailingZeros()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: []));

        // 1. Trailing zero
        var rawJson1 = $$"""
        {
            "dailyRate": 500000.0,
            "currency": "VND"
        }
        """;
        var resp1 = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(rawJson1, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var dto1 = await resp1.Content.ReadFromJsonAsync<AssetRentalRateDto>();
        Assert.Equal(500_000m, dto1?.DailyRate);

        // 2. Scientific notation
        var rawJson2 = $$"""
        {
            "dailyRate": 1e6,
            "currency": "VND"
        }
        """;
        var resp2 = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(rawJson2, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var dto2 = await resp2.Content.ReadFromJsonAsync<AssetRentalRateDto>();
        Assert.Equal(1_000_000m, dto2?.DailyRate);
    }

    [Fact]
    public async Task GetById_And_List_IncludesRentalRate()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Model With Rate");

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

        // GET detail
        var detailResp = await client.GetAsync($"api/v1/inventory/assets/{asset.Id:D}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = await detailResp.Content.ReadFromJsonAsync<InventoryAssetResponse>();
        Assert.NotNull(detail);
        Assert.NotNull(detail.RentalRate);
        Assert.Equal(100_000m, detail.RentalRate.DailyRate);
        Assert.Equal("VND", detail.RentalRate.Currency);

        // GET list
        var listResp = await client.GetAsync("api/v1/inventory/assets?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var paged = await listResp.Content.ReadFromJsonAsync<PagedResponse<InventoryAssetResponse>>();
        Assert.NotNull(paged);
        var found = paged.Items.FirstOrDefault(i => i.Id == asset.Id);
        Assert.NotNull(found);
        Assert.NotNull(found.RentalRate);
        Assert.Equal(100_000m, found.RentalRate.DailyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_OwnerWithoutManagePermission_Succeeds()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        var ownerTokenNoPerms = secFactory.CreateHumanToken(owner, permissions: []);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerTokenNoPerms);

        var putPayload = new
        {
            dailyRate = 350_000m,
            weeklyRate = 2_100_000m,
            monthlyRate = (decimal?)null,
            currency = "VND"
        };

        var response = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updatedRate = await response.Content.ReadFromJsonAsync<AssetRentalRateDto>();
        Assert.NotNull(updatedRate);
        Assert.Equal(350_000m, updatedRate.DailyRate);
        Assert.Equal(2_100_000m, updatedRate.WeeklyRate);
        Assert.Null(updatedRate.MonthlyRate); // Cleared
        Assert.Equal("VND", updatedRate.Currency);

        // Verify in DB
        await using var db = CreateDbContext();
        var rateInDb = await db.AssetRentalRates.FindAsync(asset.Id);
        Assert.NotNull(rateInDb);
        Assert.Equal(350_000m, rateInDb.DailyRate);
        Assert.Equal(2_100_000m, rateInDb.WeeklyRate);
        Assert.Null(rateInDb.MonthlyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_AdminCrossOwner_Succeeds()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();

        var adminId = Guid.NewGuid();
        var adminToken = secFactory.CreateAdminToken(adminId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var putPayload = new
        {
            dailyRate = 999_000m,
            currency = "VND"
        };

        var response = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rate = await response.Content.ReadFromJsonAsync<AssetRentalRateDto>();
        Assert.NotNull(rate);
        Assert.Equal(999_000m, rate.DailyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_OtherUserAndService_DeniedWith403()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var (app, secFactory) = CreateFactory();
        using var client = app.CreateClient();
        var putPayload = new { dailyRate = 500_000m, currency = "VND" };
        var putContent = new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json");
        // 1. Other user -> 403
        var otherToken = secFactory.CreateHumanToken(Guid.NewGuid(), permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        var otherResp = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.Forbidden, otherResp.StatusCode);

        // 2. Service principal -> 403
        var serviceToken = secFactory.CreateServiceToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
        var svcResp = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.Forbidden, svcResp.StatusCode);

        // 3. Anonymous -> 401
        client.DefaultRequestHeaders.Authorization = null;
        var anonResp = await client.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 4. Missing asset -> 404
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            secFactory.CreateHumanToken(owner, permissions: []));
        var missingResp = await client.PutAsync($"api/v1/inventory/assets/{Guid.NewGuid():D}/rental-rate", putContent);
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);
    }

    [Fact]
    public async Task UpdateRentalRate_Concurrency_TwoPutsSameSnapshot_OneSucceedsOneConflicts()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner);

        var barrier = new ConcurrencySnapshotBarrier(count: 2, timeout: TimeSpan.FromSeconds(5));

        var (app, secFactory) = CreateFactory(assetRepoFactory: provider =>
        {
            var db = provider.GetRequiredService<InventoryServiceDbContext>();
            var inner = new InventoryAssetRepository(db);
            return new RaceInterceptingAssetRepository(
                inner,
                db,
                onAfterSnapshotRead: async a =>
                {
                    if (a?.Id == asset.Id)
                    {
                        await barrier.ArriveAndAwaitAsync();
                    }
                });
        });

        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();
        var token = secFactory.CreateHumanToken(owner, permissions: []);
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var payload1 = new { dailyRate = 300_000m, currency = "VND" };
        var payload2 = new { dailyRate = 400_000m, currency = "VND" };

        var task1 = client1.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(payload1), Encoding.UTF8, "application/json"));
        var task2 = client2.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(payload2), Encoding.UTF8, "application/json"));

        var responses = await Task.WhenAll(task1, task2);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        // Exactly one 200 OK and one 409 Conflict
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));

        var okResponse = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winningDto = await okResponse.Content.ReadFromJsonAsync<AssetRentalRateDto>();
        Assert.NotNull(winningDto);

        await using var verifyDb = CreateDbContext();
        var currentRate = await verifyDb.AssetRentalRates.FindAsync(asset.Id);
        Assert.NotNull(currentRate);
        Assert.Equal(winningDto.DailyRate, currentRate.DailyRate);
    }

    [Fact]
    public async Task UpdateRentalRate_Sequential_PutAndPatch_PreservesBothChanges()
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Original FX3");

        var (app, secFactory) = CreateFactory();
        using var client1 = app.CreateClient();
        using var client2 = app.CreateClient();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Put rate
        var putPayload = new { dailyRate = 750_000m, currency = "VND" };
        var putResp = await client1.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Patch model
        var patchPayload = new { model = "Patched FX3" };
        var patchResp = await client2.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

        // Verify both changes exist together in PostgreSQL
        await using var db = CreateDbContext();
        var finalAsset = await db.Assets.Include(a => a.RentalRate).FirstOrDefaultAsync(a => a.Id == asset.Id);
        Assert.NotNull(finalAsset);
        Assert.Equal("Patched FX3", finalAsset.Model);
        Assert.NotNull(finalAsset.RentalRate);
        Assert.Equal(750_000m, finalAsset.RentalRate.DailyRate);
    }

    [Theory]
    [InlineData(true)]  // PUT wins first, PATCH conflicts and retries
    [InlineData(false)] // PATCH wins first, PUT conflicts and retries
    public async Task UpdateRentalRate_Concurrency_PutAndPatch_BothWinningOrders_RetryConflictSucceeds(bool putWinsFirst)
    {
        var owner = Guid.NewGuid();
        var asset = await SeedAssetAsync(owner, model: "Original Model");

        var snapshotBarrier = new ConcurrencySnapshotBarrier(count: 2, timeout: TimeSpan.FromSeconds(5));
        var winnerInsideLockTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (app, secFactory) = CreateFactory(assetRepoFactory: provider =>
        {
            var db = provider.GetRequiredService<InventoryServiceDbContext>();
            var inner = new InventoryAssetRepository(db);
            var httpAccessor = provider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();

            return new RaceInterceptingAssetRepository(
                inner,
                db,
                onAfterSnapshotRead: async a =>
                {
                    if (a?.Id == asset.Id)
                    {
                        await snapshotBarrier.ArriveAndAwaitAsync();

                        var method = httpAccessor.HttpContext?.Request.Method;
                        if (putWinsFirst && method == "PATCH")
                        {
                            await winnerInsideLockTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        else if (!putWinsFirst && method == "PUT")
                        {
                            await winnerInsideLockTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                },
                onInsideLockRead: () =>
                {
                    var method = httpAccessor.HttpContext?.Request.Method;
                    if (putWinsFirst && method == "PUT")
                    {
                        winnerInsideLockTcs.TrySetResult(true);
                    }
                    else if (!putWinsFirst && method == "PATCH")
                    {
                        winnerInsideLockTcs.TrySetResult(true);
                    }
                });
        });

        using var clientPut = app.CreateClient();
        using var clientPatch = app.CreateClient();
        var token = secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]);
        clientPut.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        clientPatch.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var putPayload = new { dailyRate = 888_000m, currency = "VND" };
        var patchPayload = new { model = "Concurrent Patched Model" };

        var putTask = clientPut.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
            new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));
        var patchTask = clientPatch.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
            new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"));

        var putResp = await putTask;
        var patchResp = await patchTask;

        if (putWinsFirst)
        {
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, patchResp.StatusCode);

            // Retry PATCH with new snapshot
            var retryPatchResp = await clientPatch.PatchAsync($"api/v1/inventory/assets/{asset.Id:D}",
                new StringContent(JsonSerializer.Serialize(patchPayload), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, retryPatchResp.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, putResp.StatusCode);

            // Retry PUT with new snapshot
            var retryPutResp = await clientPut.PutAsync($"api/v1/inventory/assets/{asset.Id:D}/rental-rate",
                new StringContent(JsonSerializer.Serialize(putPayload), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, retryPutResp.StatusCode);
        }

        // Verify final DB state has BOTH changes preserved
        await using var verifyDb = CreateDbContext();
        var finalAsset = await verifyDb.Assets.Include(a => a.RentalRate).FirstOrDefaultAsync(a => a.Id == asset.Id);
        Assert.NotNull(finalAsset);
        Assert.Equal("Concurrent Patched Model", finalAsset.Model);
        Assert.NotNull(finalAsset.RentalRate);
        Assert.Equal(888_000m, finalAsset.RentalRate.DailyRate);
    }

    [Fact]
    public async Task Atomicity_RateInsertFails_EntireTransactionRollsBack()
    {
        var owner = Guid.NewGuid();
        var targetAssetId = Guid.NewGuid();

        // Cài đặt PostgreSQL trigger để ép lỗi riêng bảng asset_rental_rates khi insert
        await using var triggerConn = new NpgsqlConnection(_fixture.ConnectionString);
        await triggerConn.OpenAsync();
        await using var triggerCmd = triggerConn.CreateCommand();
        triggerCmd.CommandText = $"""
            CREATE OR REPLACE FUNCTION inventory.failing_rental_rate_trigger()
            RETURNS TRIGGER AS $$
            BEGIN
                IF NEW.daily_rate = 777777 THEN
                    RAISE EXCEPTION 'TEST_FAIL_RENTAL_RATE_INSERT';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS trg_test_fail_rate ON inventory.asset_rental_rates;
            CREATE TRIGGER trg_test_fail_rate
            BEFORE INSERT ON inventory.asset_rental_rates
            FOR EACH ROW EXECUTE FUNCTION inventory.failing_rental_rate_trigger();
            """;
        await triggerCmd.ExecuteNonQueryAsync();

        try
        {
            var (app, secFactory) = CreateFactory();
            using var client = app.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                secFactory.CreateHumanToken(owner, permissions: [InventoryAccessEvaluator.PermissionAssetManage]));

            var payload = new
            {
                equipmentTypeId = Guid.NewGuid(),
                brandId = Guid.NewGuid(),
                model = "Atomic Test Asset",
                schemaVersion = 1,
                specValues = new { },
                rentalRate = new
                {
                    dailyRate = 777777m, // Triggers exception!
                    currency = "VND"
                }
            };

            var response = await client.PostAsync("api/v1/inventory/assets",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            // Xác nhận cả asset và rate đều KHÔNG được persist vào PostgreSQL
            await using var db = CreateDbContext();
            var assetExists = await db.Assets.AnyAsync(a => a.Model == "Atomic Test Asset");
            var rateExists = await db.AssetRentalRates.AnyAsync(r => r.DailyRate == 777777m);
            Assert.False(assetExists);
            Assert.False(rateExists);
        }
        finally
        {
            await using var cleanupCmd = triggerConn.CreateCommand();
            cleanupCmd.CommandText = "DROP TRIGGER IF EXISTS trg_test_fail_rate ON inventory.asset_rental_rates;";
            await cleanupCmd.ExecuteNonQueryAsync();
        }
    }
}
