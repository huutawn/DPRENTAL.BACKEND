using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.Validation;
using Dprental.InventoryService.Domain.IRepositories;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Assets;

public sealed class AssetSearchQueryValidatorTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly TestTimeProvider _clock = new(FixedNow);
    private readonly AssetSearchQueryValidator _validator;

    public AssetSearchQueryValidatorTests()
    {
        _validator = new AssetSearchQueryValidator(_clock);
    }

    [Fact]
    public void Validate_DefaultValues_ReturnsSuccessWithNormalizedQuery()
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
        var q = result.Value;
        Assert.Equal(1, q.Page);
        Assert.Equal(20, q.PageSize);
        Assert.Equal(0, q.Skip);
        Assert.Equal(20, q.Take);
        Assert.Null(q.SearchKeyword);
        Assert.Null(q.EquipmentTypeId);
        Assert.Null(q.BrandId);
        Assert.Null(q.ManufactureYearFrom);
        Assert.Null(q.ManufactureYearTo);
        Assert.Equal(AssetSortOption.CreatedDesc, q.Sort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Validate_InvalidPage_ReturnsInvalidPageError(int page)
    {
        var result = _validator.Validate(page, 20, null, null, null, null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_PAGE", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(500)]
    public void Validate_InvalidPageSize_ReturnsInvalidPageSizeError(int pageSize)
    {
        var result = _validator.Validate(1, pageSize, null, null, null, null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_PAGE_SIZE", result.Error.Code);
    }

    [Fact]
    public void Validate_OffsetOverflow_ReturnsInvalidPageError()
    {
        var result = _validator.Validate(int.MaxValue, 100, null, null, null, null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_PAGE", result.Error.Code);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Validate_WhitespaceOrEmptyKeyword_NormalizesToNull(string q)
    {
        var result = _validator.Validate(1, 20, q, null, null, null, null, null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.SearchKeyword);
    }

    [Fact]
    public void Validate_KeywordWithWhitespace_TrimsCorrectly()
    {
        var result = _validator.Validate(1, 20, "   forklift 8FD25   ", null, null, null, null, null);

        Assert.True(result.IsSuccess);
        Assert.Equal("forklift 8FD25", result.Value.SearchKeyword);
    }

    [Fact]
    public void Validate_KeywordExceeding200Chars_ReturnsError()
    {
        var longKeyword = new string('x', 201);
        var result = _validator.Validate(1, 20, longKeyword, null, null, null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public void Validate_KeywordExact200Chars_Succeeds()
    {
        var exactKeyword = new string('x', 200);
        var result = _validator.Validate(1, 20, exactKeyword, null, null, null, null, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(exactKeyword, result.Value.SearchKeyword);
    }

    [Fact]
    public void Validate_EmptyGuidEquipmentType_ReturnsError()
    {
        var result = _validator.Validate(1, 20, null, Guid.Empty, null, null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public void Validate_EmptyGuidBrandId_ReturnsError()
    {
        var result = _validator.Validate(1, 20, null, null, Guid.Empty, null, null, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Theory]
    [InlineData(1899, null)]
    [InlineData(null, 1899)]
    [InlineData(2050, null)]
    [InlineData(null, 2050)]
    [InlineData(2025, 2020)] // From > To
    public void Validate_InvalidManufactureYears_ReturnsError(int? from, int? to)
    {
        var result = _validator.Validate(1, 20, null, null, null, from, to, null);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }

    [Fact]
    public void Validate_ValidManufactureYears_Succeeds()
    {
        var result = _validator.Validate(1, 20, null, null, null, 2020, 2024, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(2020, result.Value.ManufactureYearFrom);
        Assert.Equal(2024, result.Value.ManufactureYearTo);
    }

    [Theory]
    [InlineData("created_desc", AssetSortOption.CreatedDesc)]
    [InlineData("created_asc", AssetSortOption.CreatedAsc)]
    [InlineData("model_desc", AssetSortOption.ModelDesc)]
    [InlineData("model_asc", AssetSortOption.ModelAsc)]
    [InlineData("manufacture_year_desc", AssetSortOption.ManufactureYearDesc)]
    [InlineData("manufacture_year_asc", AssetSortOption.ManufactureYearAsc)]
    [InlineData("CREATED_DESC", AssetSortOption.CreatedDesc)]
    public void Validate_SupportedSorts_NormalizedCorrectly(string sortStr, AssetSortOption expected)
    {
        var result = _validator.Validate(1, 20, null, null, null, null, null, sortStr);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Sort);
    }

    [Theory]
    [InlineData("price_asc")]
    [InlineData("random")]
    [InlineData("created_desc; DROP TABLE")]
    public void Validate_UnsupportedSort_ReturnsError(string badSort)
    {
        var result = _validator.Validate(1, 20, null, null, null, null, null, badSort);

        Assert.True(result.IsFailure);
        Assert.Equal("INVALID_QUERY_PARAMETER", result.Error.Code);
    }
}
