using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common.Enums;
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
}
