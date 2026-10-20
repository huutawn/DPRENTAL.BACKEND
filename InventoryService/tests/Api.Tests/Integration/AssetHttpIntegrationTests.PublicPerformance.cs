using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.DTOs.Assets;
using Dprental.InventoryService.Application.DTOs.Catalog;
using Dprental.InventoryService.Application.DTOs.Pagination;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class AssetHttpIntegrationTests
{
    private sealed class QueryCountingInterceptor : DbCommandInterceptor
    {
        public int SelectCount { get; private set; }
        public List<string> ExecutedSqls { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            RecordCommand(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            RecordCommand(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void RecordCommand(string sql)
        {
            var trimmed = sql.TrimStart();
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                SelectCount++;
                ExecutedSqls.Add(sql);
            }
        }

        public void Reset()
        {
            SelectCount = 0;
            ExecutedSqls.Clear();
        }
    }

    [Fact]
    public async Task GetPublicAssets_QueryAndHttpCallCounts_IndependentOfPageSize_NoNPlusOne()
    {
        var interceptor = new QueryCountingInterceptor();
        var stubCatalog = new StubCatalogClient
        {
            BatchSchemasHandler = (id, versions) => Task.FromResult(Dprental.InventoryService.Common.Result.Success(new CatalogBatchSchemasResponse(
                id,
                1,
                [
                    new CatalogBatchSchemaItemResponse(1, [
                        new CatalogBatchFieldResponse("power", "Power", "number", true, true, false, 0, 0, 1000, null, "kW", null)
                    ])
                ])))
        };
        var stubMedia = new StubMediaClient();

        var (app, secFactory) = CreateFactory(catalogClient: stubCatalog, mediaClient: stubMedia);
        var configuredApp = app.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<InventoryServiceDbContext>>();
                services.RemoveAll<InventoryServiceDbContext>();
                services.AddDbContext<InventoryServiceDbContext>(options =>
                {
                    options.UseNpgsql(_fixture.ConnectionString, npgsql =>
                        npgsql.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
                        .AddInterceptors(interceptor);
                });
            });
        });

        await using var _ = configuredApp;
        await using var __ = secFactory;
        using var client = configuredApp.CreateClient();

        var owner = Guid.NewGuid();
        var eqTypeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Seed 25 assets
        InventoryAsset? firstAsset = null;
        for (int i = 0; i < 25; i++)
        {
            var asset = await SeedAssetAsync(
                owner,
                model: $"Model_{i:D2}",
                brandId: brandId,
                eqTypeId: eqTypeId,
                specValues: "{\"power\": 55}");
            firstAsset ??= asset;
        }

        // ==========================================
        // 1. Public Detail: 4 SELECTs, 0 Catalog, 0 Media
        // ==========================================
        interceptor.Reset();
        var detailRes = await client.GetAsync($"api/v1/inventory/public/equipment-listings/{firstAsset!.EquipmentListingId:D}");
        Assert.Equal(HttpStatusCode.OK, detailRes.StatusCode);
        Assert.Equal(4, interceptor.SelectCount);
        Assert.Equal(0, stubCatalog.BatchCalls);
        Assert.Equal(0, stubMedia.VerifyCalls);
        // ==========================================
        // 2. Public List (no dynamic filters):
        // 3 SELECTs, 0 Catalog, 0 Media
        // Repeat across page sizes 1, 20, 100 -> counts stay constant!
        // ==========================================
        foreach (var pageSize in new[] { 1, 20, 100 })
        {
            interceptor.Reset();
            var listRes = await client.GetAsync($"api/v1/inventory/public/equipment-listings?page=1&pageSize={pageSize}");
            Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
            Assert.Equal(3, interceptor.SelectCount);
            Assert.Equal(0, stubCatalog.BatchCalls);
            Assert.Equal(0, stubMedia.VerifyCalls);
        }

        // ==========================================
        // 3. Public List with dynamic filters:
        // 4 SELECTs, 1 Catalog HTTP, 0 Media HTTP
        // Repeat across page sizes 1, 20, 100 -> counts stay constant!
        // ==========================================
        var filterJson = Uri.EscapeDataString("[{\"field\":\"power\",\"operator\":\"gte\",\"value\":50}]");
        int previousCatalogCalls = stubCatalog.BatchCalls;

        foreach (var pageSize in new[] { 1, 20, 100 })
        {
            interceptor.Reset();
            var listRes = await client.GetAsync($"api/v1/inventory/public/equipment-listings?equipmentTypeId={eqTypeId:D}&filters={filterJson}&page=1&pageSize={pageSize}");
            Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
            Assert.Equal(4, interceptor.SelectCount);
            Assert.Equal(previousCatalogCalls + 1, stubCatalog.BatchCalls);
            previousCatalogCalls = stubCatalog.BatchCalls;
            Assert.Equal(0, stubMedia.VerifyCalls);
        }
    }
}
