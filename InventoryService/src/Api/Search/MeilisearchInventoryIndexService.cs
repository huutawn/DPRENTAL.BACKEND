using System.Text;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Models;
using Dprental.InventoryService.Common;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppResult = Dprental.InventoryService.Common.Result;
using ResultCandidate = Dprental.InventoryService.Common.Result<Dprental.InventoryService.Application.Search.CandidateSearchResult>;

namespace Dprental.InventoryService.Api.Search;

/// <summary>
/// Triển khai IInventorySearchIndexService tại tầng Api/Search gọi Meilisearch HTTP SDK.
/// </summary>
public sealed class MeilisearchInventoryIndexService : IInventorySearchIndexService
{
    private readonly MeilisearchClient _client;
    private readonly MeilisearchOptions _options;
    private readonly ILogger<MeilisearchInventoryIndexService> _logger;

    public MeilisearchInventoryIndexService(
        MeilisearchClient client,
        IOptions<MeilisearchOptions> options,
        ILogger<MeilisearchInventoryIndexService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ResultCandidate> SearchPublicListingCandidatesAsync(
        PublicListingSearchIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Keyword))
        {
            return AppResult.Success(CandidateSearchResult.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var normalizedQ = NormalizeSearchText(query.Keyword);
            var filterClauses = new List<string>();

            if (query.EquipmentTypeId.HasValue)
            {
                filterClauses.Add($"equipmentTypeId = \"{query.EquipmentTypeId.Value}\"");
            }

            if (query.BrandId.HasValue)
            {
                filterClauses.Add($"brandId = \"{query.BrandId.Value}\"");
            }

            int candidateLimit = _options.CandidateLimit;
            var filterString = filterClauses.Count > 0 ? string.Join(" AND ", filterClauses) : null;

            var index = _client.Index(_options.PublicListingsIndex);
            var searchQuery = new SearchQuery
            {
                Filter = filterString,
                Limit = candidateLimit
            };

            var searchResult = await index.SearchAsync<PublicListingIndexDocument>(normalizedQ, searchQuery, cancellationToken);
            return ProcessHits(searchResult, candidateLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MeilisearchApiError ex) when (IsCredentialOrPermissionError(ex))
        {
            _logger.LogCritical(ex, "LỖI BẢO MẬT/CẤU HÌNH: Meilisearch credentials hoặc quyền truy cập không hợp lệ: {Code}", ex.Code);
            return Error.Unavailable("SEARCH_CREDENTIALS_INVALID", "Dịch vụ chỉ mục tìm kiếm tạm thời không khả dụng.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi truy vấn chỉ mục Meilisearch cho Public Listings với từ khóa '{Keyword}'. Chuyển sang SQL fallback.", query.Keyword);
            return Error.Failure("MEILISEARCH_QUERY_FAILED", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResultCandidate> SearchOwnerListingCandidatesAsync(
        OwnerListingSearchIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Keyword))
        {
            return AppResult.Success(CandidateSearchResult.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var normalizedQ = NormalizeSearchText(query.Keyword);
            var filterClauses = new List<string>
            {
                $"ownerUserId = \"{query.OwnerUserId}\""
            };

            if (query.EquipmentTypeId.HasValue)
            {
                filterClauses.Add($"equipmentTypeId = \"{query.EquipmentTypeId.Value}\"");
            }

            if (query.BrandId.HasValue)
            {
                filterClauses.Add($"brandId = \"{query.BrandId.Value}\"");
            }

            if (query.Status.HasValue)
            {
                filterClauses.Add($"status = {query.Status.Value}");
            }

            int candidateLimit = _options.CandidateLimit;
            var filterString = string.Join(" AND ", filterClauses);

            var index = _client.Index(_options.OwnerListingsIndex);
            var searchQuery = new SearchQuery
            {
                Filter = filterString,
                Limit = candidateLimit
            };

            var searchResult = await index.SearchAsync<OwnerListingIndexDocument>(normalizedQ, searchQuery, cancellationToken);
            return ProcessHits(searchResult, candidateLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MeilisearchApiError ex) when (IsCredentialOrPermissionError(ex))
        {
            _logger.LogCritical(ex, "LỖI BẢO MẬT/CẤU HÌNH: Meilisearch credentials hoặc quyền truy cập không hợp lệ: {Code}", ex.Code);
            return Error.Unavailable("SEARCH_CREDENTIALS_INVALID", "Dịch vụ chỉ mục tìm kiếm tạm thời không khả dụng.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi truy vấn chỉ mục Meilisearch cho Owner Listings với từ khóa '{Keyword}'. Chuyển sang SQL fallback.", query.Keyword);
            return Error.Failure("MEILISEARCH_QUERY_FAILED", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ResultCandidate> SearchOwnerAssetCandidatesAsync(
        OwnerAssetSearchIndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Keyword))
        {
            return AppResult.Success(CandidateSearchResult.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var normalizedQ = NormalizeSearchText(query.Keyword);
            var filterClauses = new List<string>
            {
                $"ownerUserId = \"{query.OwnerUserId}\""
            };

            if (query.EquipmentTypeId.HasValue)
            {
                filterClauses.Add($"equipmentTypeId = \"{query.EquipmentTypeId.Value}\"");
            }

            if (query.BrandId.HasValue)
            {
                filterClauses.Add($"brandId = \"{query.BrandId.Value}\"");
            }

            if (query.AcceptingBookings.HasValue)
            {
                filterClauses.Add($"acceptingBookings = {query.AcceptingBookings.Value.ToString().ToLowerInvariant()}");
            }

            if (query.ManufactureYearFrom.HasValue)
            {
                filterClauses.Add($"manufactureYear >= {query.ManufactureYearFrom.Value}");
            }

            if (query.ManufactureYearTo.HasValue)
            {
                filterClauses.Add($"manufactureYear <= {query.ManufactureYearTo.Value}");
            }

            int candidateLimit = _options.CandidateLimit;
            var filterString = string.Join(" AND ", filterClauses);

            var index = _client.Index(_options.OwnerAssetsIndex);
            var searchQuery = new SearchQuery
            {
                Filter = filterString,
                Limit = candidateLimit,
                MatchingStrategy = "all"
            };

            var searchResult = await index.SearchAsync<OwnerAssetIndexDocument>(normalizedQ, searchQuery, cancellationToken);
            return ProcessHits(searchResult, candidateLimit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MeilisearchApiError ex) when (IsCredentialOrPermissionError(ex))
        {
            _logger.LogCritical(ex, "LỖI BẢO MẬT/CẤU HÌNH: Meilisearch credentials hoặc quyền truy cập không hợp lệ: {Code}", ex.Code);
            return Error.Unavailable("SEARCH_CREDENTIALS_INVALID", "Dịch vụ chỉ mục tìm kiếm tạm thời không khả dụng.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi truy vấn chỉ mục Meilisearch cho Owner Assets với từ khóa '{Keyword}'. Chuyển sang SQL fallback.", query.Keyword);
            return Error.Failure("MEILISEARCH_QUERY_FAILED", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.IsHealthyAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeSearchText(string text)
    {
        return text.Trim().Normalize(NormalizationForm.FormC);
    }

    private static bool IsCredentialOrPermissionError(MeilisearchApiError ex)
    {
        return string.Equals(ex.Code, "invalid_api_key", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ex.Code, "missing_authorization_header", StringComparison.OrdinalIgnoreCase);
    }

    private static ResultCandidate ProcessHits<TDoc>(
        ISearchable<TDoc> searchResult,
        int candidateLimit)
    {
        var hits = searchResult.Hits;
        int? totalHits = searchResult is PaginatedSearchResult<TDoc> psr ? psr.TotalHits : null;
        int? estimatedTotalHits = searchResult is SearchResult<TDoc> sr ? sr.EstimatedTotalHits : null;

        var candidateIds = new List<Guid>(hits.Count);
        foreach (var hit in hits)
        {
            string? idString = hit switch
            {
                PublicListingIndexDocument pl => pl.Id,
                OwnerListingIndexDocument ol => ol.Id,
                OwnerAssetIndexDocument oa => oa.Id,
                _ => null
            };

            if (idString != null && Guid.TryParse(idString, out var guid))
            {
                candidateIds.Add(guid);
            }
        }

        int reportedTotal = totalHits ?? estimatedTotalHits ?? hits.Count;
        // Nếu số hits chạm hoặc vượt limit, hoặc tổng ước tính lớn hơn limit -> kết quả bị cắt
        bool isTruncated = hits.Count >= candidateLimit || (reportedTotal > candidateLimit);

        return AppResult.Success(new CandidateSearchResult(
            CandidateIds: candidateIds,
            TotalHits: reportedTotal,
            IsTruncated: isTruncated,
            Backend: "meilisearch"));
    }
}
