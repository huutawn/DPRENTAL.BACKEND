using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using Dprental.InventoryService.Api.Search;
using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Application.Search;
using Dprental.InventoryService.Application.Search.Configuration;
using Dprental.InventoryService.Application.Search.Mapping;
using Dprental.InventoryService.Application.Search.Models;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Search;

public sealed class MeilisearchSearchUnitTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly TestTimeProvider _clock = new(FixedNow);
    private readonly AssetSearchQueryValidator _validator;

    public MeilisearchSearchUnitTests()
    {
        _validator = new AssetSearchQueryValidator(_clock);
    }

    [Fact]
    public void SearchExecution_EncapsulatesPayloadAndBackendHeader()
    {
        var exec = new SearchExecution<string>("payload_data", "meilisearch");
        Assert.Equal("payload_data", exec.Value);
        Assert.Equal("meilisearch", exec.SearchBackend);
    }

    [Fact]
    public void SearchProjectionMapper_ToPublicListingDocument_SanitizesSensitiveFields()
    {
        var listingId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        var listing = EquipmentListing.Create(
            listingId,
            ownerId,
            typeId,
            brandId,
            "Máy ảnh Sony Alpha A7 IV",
            "Mô tả chi tiết",
            "ILCE-7M4",
            schemaVersion: 1,
            specValues: "{}",
            dailyRate: 500000,
            weeklyRate: null,
            monthlyRate: null,
            currency: "VND",
            now: FixedNow,
            manufactureYear: 2024).Value;

        var doc = SearchProjectionMapper.ToPublicListingDocument(listing);

        Assert.Equal(listingId.ToString("D"), doc.Id);
        Assert.Equal("Máy ảnh Sony Alpha A7 IV", doc.Name);
        Assert.Equal("ILCE-7M4", doc.Model);
        Assert.Equal(typeId.ToString("D"), doc.EquipmentTypeId);
        Assert.Equal(brandId.ToString("D"), doc.BrandId);

        var docProps = typeof(PublicListingIndexDocument).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("OwnerUserId", docProps);
        Assert.DoesNotContain("SerialNumber", docProps);
        Assert.DoesNotContain("AssetId", docProps);
    }

    [Fact]
    public void SearchIndexDefinition_TypoTolerance_DisablesTypoOnSerial()
    {
        Assert.Contains("serialNumber", SearchIndexDefinition.OwnerAssetDisabledTypoAttributes);
        Assert.Contains("-", SearchIndexDefinition.OwnerAssetNonSeparatorTokens);
        Assert.Contains("_", SearchIndexDefinition.OwnerAssetNonSeparatorTokens);
    }

    [Fact]
    public void MeilisearchOptions_Validation_DetectsCollidingIndexNames()
    {
        var options = new MeilisearchOptions
        {
            Url = "http://localhost:7700",
            PublicListingsIndex = "duplicate_index",
            OwnerListingsIndex = "duplicate_index",
            OwnerAssetsIndex = "assets_index"
        };

        var context = new ValidationContext(options);
        var results = options.Validate(context).ToList();

        Assert.Contains(results, r => r.ErrorMessage != null && r.ErrorMessage.Contains("trùng nhau"));
    }

    [Fact]
    public void Validate_KeywordWithoutSort_DefaultsToRelevance()
    {
        var result = _validator.Validate(
            page: 1,
            pageSize: 20,
            q: "sony a7",
            equipmentTypeId: null,
            brandId: null,
            manufactureYearFrom: null,
            manufactureYearTo: null,
            sort: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.Relevance, result.Value.Sort);
        Assert.Equal("sony a7", result.Value.SearchKeyword);
    }

    [Fact]
    public void Validate_NoKeywordWithoutSort_DefaultsToCreatedDesc()
    {
        var result = _validator.Validate(
            page: 1,
            pageSize: 20,
            q: null,
            equipmentTypeId: null,
            brandId: null,
            manufactureYearFrom: null,
            manufactureYearTo: null,
            sort: null);

        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.CreatedDesc, result.Value.Sort);
    }

    [Fact]
    public void Validate_KeywordWithExplicitSort_ExplicitSortOverridesRelevance()
    {
        var result = _validator.Validate(
            page: 1,
            pageSize: 20,
            q: "sony",
            equipmentTypeId: null,
            brandId: null,
            manufactureYearFrom: null,
            manufactureYearTo: null,
            sort: "created_asc");

        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.CreatedAsc, result.Value.Sort);
    }

    [Fact]
    public void Validate_ExplicitRelevanceSort_Accepted()
    {
        var result = _validator.Validate(
            page: 1,
            pageSize: 20,
            q: "canon",
            equipmentTypeId: null,
            brandId: null,
            manufactureYearFrom: null,
            manufactureYearTo: null,
            sort: "relevance");

        Assert.True(result.IsSuccess);
        Assert.Equal(AssetSortOption.Relevance, result.Value.Sort);
    }

    [Fact]
    public void VietnameseText_Normalization_ProducesConsistentFormC()
    {
        string precomposed = "Đèn quay phim";
        string decomposed = precomposed.Normalize(NormalizationForm.FormD);

        Assert.NotEqual(precomposed, decomposed);
        Assert.Equal(precomposed, decomposed.Normalize(NormalizationForm.FormC));
    }

    [Fact]
    public void CandidateSearchResult_TruncationDetection_FlagsTruncatedResults()
    {
        var candidateIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var normalResult = new CandidateSearchResult(candidateIds, TotalHits: 2, IsTruncated: false);
        Assert.False(normalResult.IsTruncated);

        var truncatedResult = new CandidateSearchResult(candidateIds, TotalHits: 5000, IsTruncated: true);
        Assert.True(truncatedResult.IsTruncated);
    }
}
