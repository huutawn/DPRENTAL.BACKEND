using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Configuration;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Api.Search;

/// <summary>
/// Khởi tạo và cấu hình các chỉ mục Meilisearch cho InventoryService theo quy chuẩn nghiệp vụ duy nhất.
/// </summary>
public sealed class MeilisearchIndexProvisioner : IInventoryIndexProvisioner
{
    private readonly MeilisearchClient _client;
    private readonly MeilisearchOptions _options;
    private readonly ILogger<MeilisearchIndexProvisioner> _logger;

    public MeilisearchIndexProvisioner(
        MeilisearchClient client,
        IOptions<MeilisearchOptions> options,
        ILogger<MeilisearchIndexProvisioner> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureIndexesAndSettingsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Khởi tạo và đồng bộ cấu hình chỉ mục Meilisearch...");

        cancellationToken.ThrowIfCancellationRequested();

        await ConfigurePublicListingsIndexAsync(cancellationToken);
        await ConfigureOwnerListingsIndexAsync(cancellationToken);
        await ConfigureOwnerAssetsIndexAsync(cancellationToken);

        _logger.LogInformation("Hoàn tất cấu hình chỉ mục Meilisearch.");
    }

    private async Task ConfigurePublicListingsIndexAsync(CancellationToken cancellationToken)
    {
        var indexUid = _options.PublicListingsIndex;
        var index = await GetOrCreateIndexAsync(indexUid, "id", cancellationToken);

        int maxHits = Math.Max(100000, _options.CandidateLimit);
        var settings = new Settings
        {
            SearchableAttributes = SearchIndexDefinition.PublicSearchableAttributes,
            FilterableAttributes = SearchIndexDefinition.PublicFilterableAttributes.Select(s => (FilterableAttribute)s),
            SortableAttributes = SearchIndexDefinition.PublicSortableAttributes,
            Pagination = new Pagination { MaxTotalHits = maxHits },
            TypoTolerance = new TypoTolerance
            {
                Enabled = true,
                MinWordSizeForTypos = new TypoTolerance.TypoSize { OneTypo = 5, TwoTypos = 9 }
            }
        };

        var task = await index.UpdateSettingsAsync(settings, cancellationToken);
        await WaitForTaskStrictAsync(task.TaskUid, cancellationToken);
    }

    private async Task ConfigureOwnerListingsIndexAsync(CancellationToken cancellationToken)
    {
        var indexUid = _options.OwnerListingsIndex;
        var index = await GetOrCreateIndexAsync(indexUid, "id", cancellationToken);

        int maxHits = Math.Max(100000, _options.CandidateLimit);
        var settings = new Settings
        {
            SearchableAttributes = SearchIndexDefinition.OwnerListingSearchableAttributes,
            FilterableAttributes = SearchIndexDefinition.OwnerListingFilterableAttributes.Select(s => (FilterableAttribute)s),
            SortableAttributes = SearchIndexDefinition.OwnerListingSortableAttributes,
            Pagination = new Pagination { MaxTotalHits = maxHits },
            TypoTolerance = new TypoTolerance
            {
                Enabled = true,
                MinWordSizeForTypos = new TypoTolerance.TypoSize { OneTypo = 5, TwoTypos = 9 }
            }
        };

        var task = await index.UpdateSettingsAsync(settings, cancellationToken);
        await WaitForTaskStrictAsync(task.TaskUid, cancellationToken);
    }

    private async Task ConfigureOwnerAssetsIndexAsync(CancellationToken cancellationToken)
    {
        var indexUid = _options.OwnerAssetsIndex;
        var index = await GetOrCreateIndexAsync(indexUid, "id", cancellationToken);

        int maxHits = Math.Max(100000, _options.CandidateLimit);
        var settings = new Settings
        {
            SearchableAttributes = SearchIndexDefinition.OwnerAssetSearchableAttributes,
            FilterableAttributes = SearchIndexDefinition.OwnerAssetFilterableAttributes.Select(s => (FilterableAttribute)s),
            SortableAttributes = SearchIndexDefinition.OwnerAssetSortableAttributes,
            NonSeparatorTokens = SearchIndexDefinition.OwnerAssetNonSeparatorTokens.ToList(),
            Pagination = new Pagination { MaxTotalHits = maxHits },
            TypoTolerance = new TypoTolerance
            {
                Enabled = true,
                MinWordSizeForTypos = new TypoTolerance.TypoSize { OneTypo = 5, TwoTypos = 9 },
                DisableOnAttributes = SearchIndexDefinition.OwnerAssetDisabledTypoAttributes
            }
        };

        var task = await index.UpdateSettingsAsync(settings, cancellationToken);
        await WaitForTaskStrictAsync(task.TaskUid, cancellationToken);
    }

    private async Task<Meilisearch.Index> GetOrCreateIndexAsync(string indexUid, string primaryKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetIndexAsync(indexUid, cancellationToken);
        }
        catch (MeilisearchApiError ex) when (ex.Code == "index_not_found")
        {
            var task = await _client.CreateIndexAsync(indexUid, primaryKey, cancellationToken);
            await WaitForTaskStrictAsync(task.TaskUid, cancellationToken);
            return await _client.GetIndexAsync(indexUid, cancellationToken);
        }
    }

    private async Task WaitForTaskStrictAsync(int taskUid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        double timeoutMs = TimeSpan.FromSeconds(_options.TaskDeadlineSeconds).TotalMilliseconds;
        var task = await _client.WaitForTaskAsync(taskUid, timeoutMs: timeoutMs, cancellationToken: cancellationToken);
        if (task.Status != TaskInfoStatus.Succeeded)
        {
            var errorMsg = task.Error != null ? string.Join(", ", task.Error.Select(kv => $"{kv.Key}: {kv.Value}")) : "Task failed";
            throw new InvalidOperationException($"Meilisearch task {taskUid} kết thúc với trạng thái {task.Status}: {errorMsg}");
        }
    }
}
