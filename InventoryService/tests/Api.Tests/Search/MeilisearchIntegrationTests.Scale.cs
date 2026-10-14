using System.Diagnostics;
using System.Text.Json;
using Dprental.InventoryService.Application.Search.Adapters;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed partial class MeilisearchIntegrationTests
{
    private static readonly JsonSerializerOptions BenchmarkJsonOptions = new() { WriteIndented = true };

    [Fact]
    public async Task Scale_BackfillAndCandidateSql_ReturnExactCountsAndBoundedCandidates()
    {
        int listings = Environment.GetEnvironmentVariable("INVENTORY_SEARCH_SCALE_LISTINGS") is { } count
            ? int.Parse(count, System.Globalization.CultureInfo.InvariantCulture) : 1000;
        Assert.InRange(listings, 1000, 100000);
        int assets = listings * 10;
        var owner = Guid.NewGuid();
        var type = Guid.NewGuid();
        var brand = Guid.NewGuid();
        _db.Database.SetCommandTimeout(120);
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.equipment_listings
                (id, owner_user_id, equipment_type_id, brand_id, name, model, description,
                 schema_version, spec_values, media_ids, manufacture_year, daily_rate, currency, status, created_at, updated_at)
            SELECT md5('listing-' || g)::uuid, {owner}, {type}, {brand},
                   'Máy ảnh Alpha ' || g, 'Alpha-' || g, 'Benchmark camera',
                   1, jsonb_build_object(), ARRAY[]::uuid[], 2024, 100000, 'VND', 2, now(), now()
            FROM generate_series(1, {listings}) g;
            INSERT INTO inventory.assets
                (id, equipment_listing_id, asset_code, serial_number, manufacture_year, accepting_bookings, created_at, updated_at)
            SELECT md5('asset-' || g)::uuid, md5('listing-' || ((g - 1) / 10 + 1))::uuid,
                   'AST-' || md5('asset-' || g), 'SN-BENCH-' || lpad(g::text, 9, '0'), 2024, TRUE, now(), now()
            FROM generate_series(1, {assets}) g;
            ANALYZE inventory.equipment_listings;
            ANALYZE inventory.assets;
            """);
        await using var services = BuildSearchProvider();
        var backfill = new MeilisearchBackfillService(services.GetRequiredService<IServiceScopeFactory>(), _client, Options.Create(_options));
        var watch = Stopwatch.StartNew();
        var result = await backfill.BackfillAsync();
        double backfillSeconds = watch.Elapsed.TotalSeconds;
        Assert.Equal(listings, result.PublicListingsCount);
        Assert.Equal(listings, result.OwnerListingsCount);
        Assert.Equal(assets, result.OwnerAssetsCount);
        var broad = await _searchService.SearchPublicListingCandidatesAsync(new("camera"));
        Assert.True(broad.IsSuccess);
        Assert.Equal(listings >= _options.CandidateLimit, broad.Value.IsTruncated);
        Assert.True(broad.Value.CandidateIds.Count <= _options.CandidateLimit);
        var serial = await _searchService.SearchOwnerAssetCandidatesAsync(new(owner, "SN-BENCH-000000001"));
        Assert.True(serial.IsSuccess);
        Assert.Single(serial.Value.CandidateIds);
        var repository = new EquipmentListingRepository(_db);
        var timings = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            watch.Restart();
            var candidates = await _searchService.SearchPublicListingCandidatesAsync(new("999"));
            Assert.True(candidates.IsSuccess);
            Assert.False(candidates.Value.IsTruncated);
            var page = await repository.SearchPublicAsync(new EquipmentListingSearchCriteria(
                new AssetSearchFilters(Sort: AssetSortOption.Relevance, CandidateIds: candidates.Value.CandidateIds),
                availabilityFrom: new DateOnly(2026, 11, 1), availabilityTo: new DateOnly(2026, 11, 2), requestedQuantity: 1));
            Assert.Equal(candidates.Value.CandidateIds.Count, page.TotalCount);
            Assert.All(page.Items, item => Assert.Equal(10, item.AvailableQuantity));
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        var report = new
        {
            listings,
            assets,
            samples = timings.Count,
            backfillSeconds,
            searchAndSqlP50Ms = timings[timings.Count / 2],
            searchAndSqlP95Ms = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1],
            candidateLimit = _options.CandidateLimit,
            measuredAtUtc = DateTimeOffset.UtcNow
        };
        if (Environment.GetEnvironmentVariable("INVENTORY_SEARCH_BENCHMARK_REPORT") is { } path)
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, BenchmarkJsonOptions));
    }
}
