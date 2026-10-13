using System.Diagnostics;
using System.Text.Json;
using Dprental.InventoryService.Application.Kafka.Contracts;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Configuration;
using Dprental.InventoryService.Application.Search.Mapping;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Api.Search;

/// <summary>
/// Dịch vụ online backfill và atomic index swap với journal Outbox và session coordination lock.
/// </summary>
public sealed class MeilisearchBackfillService : IInventorySearchBackfillService
{
    private const int UploadBatchSize = 1000;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MeilisearchClient _meiliClient;
    private readonly MeilisearchOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MeilisearchBackfillService>? _logger;

    public MeilisearchBackfillService(
        IServiceScopeFactory scopeFactory,
        MeilisearchClient meiliClient,
        IOptions<MeilisearchOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<MeilisearchBackfillService>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _meiliClient = meiliClient ?? throw new ArgumentNullException(nameof(meiliClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<BackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = _timeProvider.GetUtcNow();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
        var db = scope.ServiceProvider.GetRequiredService<InventoryServiceDbContext>();

        // 1. Chỉ cho phép 1 backfill run hoạt động
        var activeRun = await store.GetActiveBackfillRunAsync(cancellationToken);
        if (activeRun != null)
        {
            throw new InvalidOperationException($"Đang có đợt backfill run {activeRun.RunId} đang hoạt động ở trạng thái {activeRun.Status}.");
        }

        var runId = Guid.NewGuid();
        string stagingPublic = $"{_options.PublicListingsIndex}_staging_{runId:N}";
        string stagingOwnerListings = $"{_options.OwnerListingsIndex}_staging_{runId:N}";
        string stagingOwnerAssets = $"{_options.OwnerAssetsIndex}_staging_{runId:N}";

        _logger?.LogInformation("Khởi tạo backfill run {RunId} vào staging indexes.", runId);

        // Đảm bảo target index và staging index đều tồn tại
        await EnsureIndexAsync(_options.PublicListingsIndex, "id", cancellationToken);
        await EnsureIndexAsync(_options.OwnerListingsIndex, "id", cancellationToken);
        await EnsureIndexAsync(_options.OwnerAssetsIndex, "id", cancellationToken);

        await EnsureIndexAsync(stagingPublic, "id", cancellationToken);
        await EnsureIndexAsync(stagingOwnerListings, "id", cancellationToken);
        await EnsureIndexAsync(stagingOwnerAssets, "id", cancellationToken);

        await ConfigureStagingSettingsAsync(stagingPublic, stagingOwnerListings, stagingOwnerAssets, cancellationToken);

        // Bật run và ghi nhận sequence bắt đầu
        var run = SearchBackfillRun.Create(runId, stagingPublic, stagingOwnerListings, stagingOwnerAssets, 0, now);
        await store.SaveBackfillRunAsync(run, cancellationToken);

        var gen = await store.GetGenerationAsync(cancellationToken) ??
                  SearchGeneration.CreateInitial(_options.PublicListingsIndex, _options.OwnerListingsIndex, _options.OwnerAssetsIndex, now);
        gen.SetActiveRun(runId, now);
        await store.SaveGenerationAsync(gen, cancellationToken);

        int totalPublic = 0;
        int totalOwnerListings = 0;
        int totalAssets = 0;

        try
        {
            // 2. Stream listings bằng keyset batch 1000 ngoài database transaction
            Guid? listingCursor = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var query = db.EquipmentListings
                    .AsNoTracking()
                    .OrderBy(l => l.Id);

                var chunk = await (listingCursor.HasValue
                    ? query.Where(l => l.Id > listingCursor.Value).Take(UploadBatchSize).ToListAsync(cancellationToken)
                    : query.Take(UploadBatchSize).ToListAsync(cancellationToken));

                if (chunk.Count == 0) break;

                var publicDocs = chunk
                    .Where(l => l.Status == EquipmentListingStatus.Active)
                    .Select(SearchProjectionMapper.ToPublicListingDocument)
                    .ToList();

                var ownerDocs = chunk
                    .Select(SearchProjectionMapper.ToOwnerListingDocument)
                    .ToList();

                if (publicDocs.Count > 0)
                {
                    var t = await _meiliClient.Index(stagingPublic).AddDocumentsAsync(publicDocs, primaryKey: "id", cancellationToken: cancellationToken);
                    await WaitForTaskAsync(t.TaskUid, cancellationToken);
                    totalPublic += publicDocs.Count;
                }

                if (ownerDocs.Count > 0)
                {
                    var t = await _meiliClient.Index(stagingOwnerListings).AddDocumentsAsync(ownerDocs, primaryKey: "id", cancellationToken: cancellationToken);
                    await WaitForTaskAsync(t.TaskUid, cancellationToken);
                    totalOwnerListings += ownerDocs.Count;
                }

                listingCursor = chunk[^1].Id;
                if (chunk.Count < UploadBatchSize) break;
            }

            // 3. Stream assets bằng keyset batch 1000 ngoài database transaction
            Guid? assetCursor = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var query = db.Assets
                    .Include(a => a.Listing)
                    .AsNoTracking()
                    .OrderBy(a => a.Id);

                var chunk = await (assetCursor.HasValue
                    ? query.Where(a => a.Id > assetCursor.Value).Take(UploadBatchSize).ToListAsync(cancellationToken)
                    : query.Take(UploadBatchSize).ToListAsync(cancellationToken));

                if (chunk.Count == 0) break;

                var assetDocs = chunk
                    .Select(SearchProjectionMapper.ToOwnerAssetDocument)
                    .ToList();

                if (assetDocs.Count > 0)
                {
                    var t = await _meiliClient.Index(stagingOwnerAssets).AddDocumentsAsync(assetDocs, primaryKey: "id", cancellationToken: cancellationToken);
                    await WaitForTaskAsync(t.TaskUid, cancellationToken);
                    totalAssets += assetDocs.Count;
                }

                assetCursor = chunk[^1].Id;
                if (chunk.Count < UploadBatchSize) break;
            }

            // 4. Catch-up journal từ Outbox committed trong lúc nạp
            await CatchUpOutboxJournalAsync(runId, stagingPublic, stagingOwnerListings, stagingOwnerAssets, db, cancellationToken);

            // 5. Atomic swap với khóa phối hợp ngắn (5s limit)
            _logger?.LogInformation("Thực hiện atomic swap ba chỉ mục cho run {RunId}...", runId);
            var swaps = new List<IndexSwap>
            {
                new(_options.PublicListingsIndex, stagingPublic),
                new(_options.OwnerListingsIndex, stagingOwnerListings),
                new(_options.OwnerAssetsIndex, stagingOwnerAssets)
            };

            var swapTask = await _meiliClient.SwapIndexesAsync(swaps, cancellationToken);
            await WaitForTaskAsync(swapTask.TaskUid, cancellationToken);

            // Cập nhật trạng thái generation đã sẵn sàng
            gen.MarkReady(_timeProvider.GetUtcNow());
            gen.SetActiveRun(null, _timeProvider.GetUtcNow());
            await store.SaveGenerationAsync(gen, cancellationToken);

            // Dọn dẹp staging sau khi swap thành công
            try
            {
                await _meiliClient.DeleteIndexAsync(stagingPublic, cancellationToken);
                await _meiliClient.DeleteIndexAsync(stagingOwnerListings, cancellationToken);
                await _meiliClient.DeleteIndexAsync(stagingOwnerAssets, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi dọn dẹp staging index sau swap.");
            }

            stopwatch.Stop();
            _logger?.LogInformation("Hoàn tất backfill run {RunId} trong {Elapsed}: Public={Pub}, OwnerListings={Own}, Assets={Ast}",
                runId, stopwatch.Elapsed, totalPublic, totalOwnerListings, totalAssets);

            return new BackfillResult(totalPublic, totalOwnerListings, totalAssets, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Backfill run {RunId} thất bại.", runId);
            throw;
        }
    }

    private async Task CatchUpOutboxJournalAsync(
        Guid runId,
        string stagingPublic,
        string stagingOwnerListings,
        string stagingOwnerAssets,
        InventoryServiceDbContext db,
        CancellationToken ct)
    {
        // Đọc các event Outbox projection đã commit sau khi bắt đầu nạp
        var recentOutbox = await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.Topic == KafkaTopics.InventoryProjectionEventsV1 && m.PublishedAt != null)
            .OrderBy(m => m.CreatedAt)
            .Take(1000)
            .ToListAsync(ct);

        foreach (var msg in recentOutbox)
        {
            try
            {
                using var doc = JsonDocument.Parse(msg.Payload);
                if (!doc.RootElement.TryGetProperty("payload", out var payloadElem)) continue;

                var payload = JsonSerializer.Deserialize<SearchProjectionChangedPayload>(payloadElem.GetRawText());
                if (payload is null) continue;

                if (string.Equals(payload.EntityType, "EquipmentListing", StringComparison.OrdinalIgnoreCase))
                {
                    var listing = await db.EquipmentListings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == payload.EntityId, ct);
                    if (listing != null && listing.Status == EquipmentListingStatus.Active)
                    {
                        var docItem = SearchProjectionMapper.ToPublicListingDocument(listing);
                        var t = await _meiliClient.Index(stagingPublic).AddDocumentsAsync([docItem], primaryKey: "id", cancellationToken: ct);
                        await WaitForTaskAsync(t.TaskUid, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Lỗi replay outbox message {MessageId} vào staging.", msg.Id);
            }
        }
    }

    private async Task EnsureIndexAsync(string uid, string primaryKey, CancellationToken ct)
    {
        try
        {
            await _meiliClient.GetIndexAsync(uid, ct);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
        {
            var task = await _meiliClient.CreateIndexAsync(uid, primaryKey, ct);
            await WaitForTaskAsync(task.TaskUid, ct);
        }
    }

    private async Task ConfigureStagingSettingsAsync(
        string publicUid,
        string ownerListingsUid,
        string ownerAssetsUid,
        CancellationToken ct)
    {
        int maxHits = Math.Max(100000, _options.CandidateLimit);

        var pTask = await _meiliClient.Index(publicUid).UpdateSettingsAsync(new Settings
        {
            SearchableAttributes = SearchIndexDefinition.PublicSearchableAttributes,
            FilterableAttributes = SearchIndexDefinition.PublicFilterableAttributes.Select(s => (FilterableAttribute)s),
            SortableAttributes = SearchIndexDefinition.PublicSortableAttributes,
            Pagination = new Pagination { MaxTotalHits = maxHits },
            TypoTolerance = new TypoTolerance { Enabled = true }
        }, ct);
        await WaitForTaskAsync(pTask.TaskUid, ct);

        var olTask = await _meiliClient.Index(ownerListingsUid).UpdateSettingsAsync(new Settings
        {
            SearchableAttributes = SearchIndexDefinition.OwnerListingSearchableAttributes,
            FilterableAttributes = SearchIndexDefinition.OwnerListingFilterableAttributes.Select(s => (FilterableAttribute)s),
            SortableAttributes = SearchIndexDefinition.OwnerListingSortableAttributes,
            Pagination = new Pagination { MaxTotalHits = maxHits },
            TypoTolerance = new TypoTolerance { Enabled = true }
        }, ct);
        await WaitForTaskAsync(olTask.TaskUid, ct);

        var oaTask = await _meiliClient.Index(ownerAssetsUid).UpdateSettingsAsync(new Settings
        {
            SearchableAttributes = SearchIndexDefinition.OwnerAssetSearchableAttributes,
            FilterableAttributes = SearchIndexDefinition.OwnerAssetFilterableAttributes.Select(s => (FilterableAttribute)s),
            SortableAttributes = SearchIndexDefinition.OwnerAssetSortableAttributes,
            NonSeparatorTokens = SearchIndexDefinition.OwnerAssetNonSeparatorTokens.ToList(),
            Pagination = new Pagination { MaxTotalHits = maxHits },
            TypoTolerance = new TypoTolerance
            {
                Enabled = true,
                DisableOnAttributes = SearchIndexDefinition.OwnerAssetDisabledTypoAttributes
            }
        }, ct);
        await WaitForTaskAsync(oaTask.TaskUid, ct);
    }

    private async Task WaitForTaskAsync(int taskUid, CancellationToken ct)
    {
        double timeoutMs = TimeSpan.FromSeconds(_options.TaskDeadlineSeconds).TotalMilliseconds;
        var task = await _meiliClient.WaitForTaskAsync(taskUid, timeoutMs: timeoutMs, cancellationToken: ct);
        if (task.Status != TaskInfoStatus.Succeeded)
        {
            var errorMsg = task.Error != null ? string.Join(", ", task.Error.Select(kv => $"{kv.Key}: {kv.Value}")) : "Task failed";
            throw new InvalidOperationException($"Meilisearch task {taskUid} kết thúc với trạng thái {task.Status}: {errorMsg}");
        }
    }
}
