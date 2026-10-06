using System.Text.Json;
using CatalogService.Application.DTOs.Category;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class CategoryUnitAndValidationTests
{
    private static readonly IOptions<CatalogDiscoveryOptions> DefaultDiscoveryOptions =
        Options.Create(new CatalogDiscoveryOptions { MaxKeywordLength = 100 });

    private readonly CategoryListQueryValidator _validator = new(DefaultDiscoveryOptions);

    [Theory]
    [InlineData(1, 20, null, null)]
    [InlineData(1, 100, "camera", "newest")]
    [InlineData(5, 10, "  sound  ", "name_asc")]
    [InlineData(1, 1, "lens", "name_desc")]
    [InlineData(1, 50, "mic", "NEWEST")]
    [InlineData(1, 50, "mic", "NAME_ASC")]
    public void ListQueryValidator_AcceptsValidParameters(int page, int pageSize, string? keyword, string? sort)
    {
        Result result = _validator.Validate(page, pageSize, keyword, sort);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(0, 20, CatalogErrorCode.Paging.InvalidPage)]
    [InlineData(-1, 20, CatalogErrorCode.Paging.InvalidPage)]
    [InlineData(1, 0, CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData(1, -5, CatalogErrorCode.Paging.InvalidPageSize)]
    [InlineData(1, 101, CatalogErrorCode.Paging.InvalidPageSize)]
    public void ListQueryValidator_RejectsInvalidPaging(int page, int pageSize, string expectedErrorCode)
    {
        Result result = _validator.Validate(page, pageSize, null, null);
        Assert.True(result.IsFailure);
        Assert.Equal(expectedErrorCode, result.Error.Code);
    }

    [Fact]
    public void ListQueryValidator_RejectsOffsetOverflow()
    {
        Result result = _validator.Validate(int.MaxValue, 100, null, null);
        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Paging.Overflow, result.Error.Code);
    }

    [Theory]
    [InlineData("camera\0")]
    [InlineData("\0sound")]
    [InlineData("cam\0era")]
    public void ListQueryValidator_RejectsNulCharacterInKeyword(string keyword)
    {
        Result result = _validator.Validate(1, 20, keyword, null);
        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Category.InvalidKeyword, result.Error.Code);
    }

    [Fact]
    public void ListQueryValidator_RejectsKeywordExceedingMaxLength()
    {
        string oversized = new('A', 101);
        Result result = _validator.Validate(1, 20, oversized, null);
        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Category.InvalidKeyword, result.Error.Code);
    }

    [Fact]
    public void ListQueryValidator_AcceptsKeywordAtMaxLength()
    {
        string maxKeyword = new('A', 100);
        Result result = _validator.Validate(1, 20, maxKeyword, null);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("invalid_sort")]
    [InlineData("id_asc")]
    [InlineData("created_at")]
    [InlineData("oldest")]
    public void ListQueryValidator_RejectsInvalidSort(string sort)
    {
        Result result = _validator.Validate(1, 20, null, sort);
        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Category.InvalidSort, result.Error.Code);
    }

    // ========================================================
    // Category Entity State Transition Tests
    // ========================================================

    [Fact]
    public void Category_DefaultState_IsActiveWithNullUpdatedAt()
    {
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Cameras",
            Slug = "cameras"
        };

        Assert.True(category.IsActive);
        Assert.Null(category.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, category.CreatedAtUtc.Kind);
    }

    [Fact]
    public void Category_Disable_WhenActive_SetsInactiveAndUpdatesTimestamp()
    {
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Cameras",
            Slug = "cameras",
            IsActive = true
        };

        DateTime t1 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        bool changed = category.Disable(t1);

        Assert.True(changed);
        Assert.False(category.IsActive);
        Assert.Equal(t1, category.UpdatedAtUtc);
    }

    [Fact]
    public void Category_Disable_WhenAlreadyInactive_ReturnsFalseAndPreservesTimestamp()
    {
        DateTime t1 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Cameras",
            Slug = "cameras",
            IsActive = false,
            UpdatedAtUtc = t1
        };

        DateTime t2 = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);
        bool changed = category.Disable(t2);

        Assert.False(changed);
        Assert.False(category.IsActive);
        Assert.Equal(t1, category.UpdatedAtUtc);
    }

    [Fact]
    public void Category_Enable_WhenInactive_SetsActiveAndUpdatesTimestamp()
    {
        DateTime t1 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Cameras",
            Slug = "cameras",
            IsActive = false,
            UpdatedAtUtc = t1
        };

        DateTime t2 = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);
        bool changed = category.Enable(t2);

        Assert.True(changed);
        Assert.True(category.IsActive);
        Assert.Equal(t2, category.UpdatedAtUtc);
    }

    [Fact]
    public void Category_Enable_WhenAlreadyActive_ReturnsFalseAndPreservesTimestamp()
    {
        DateTime t1 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Cameras",
            Slug = "cameras",
            IsActive = true,
            UpdatedAtUtc = t1
        };

        DateTime t2 = new(2026, 10, 2, 14, 0, 0, DateTimeKind.Utc);
        bool changed = category.Enable(t2);

        Assert.False(changed);
        Assert.True(category.IsActive);
        Assert.Equal(t1, category.UpdatedAtUtc);
    }

    [Fact]
    public void Category_TransitionStatus_FollowsSameRuleForActiveAndInactive()
    {
        DateTime t1 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Audio",
            Slug = "audio",
            IsActive = true
        };

        // Chuyển sang true khi đang true -> false
        Assert.False(category.TransitionStatus(true, t1));
        Assert.Null(category.UpdatedAtUtc);

        // Chuyển sang false khi đang true -> true
        Assert.True(category.TransitionStatus(false, t1));
        Assert.False(category.IsActive);
        Assert.Equal(t1, category.UpdatedAtUtc);

        // Chuyển sang false khi đang false -> false
        DateTime t2 = new(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(category.TransitionStatus(false, t2));
        Assert.Equal(t1, category.UpdatedAtUtc);

        // Chuyển sang true khi đang false -> true
        Assert.True(category.TransitionStatus(true, t2));
        Assert.True(category.IsActive);
        Assert.Equal(t2, category.UpdatedAtUtc);
    }

    [Fact]
    public void Category_Touch_UpdatesTimestampToUtc()
    {
        Category category = new()
        {
            Id = Guid.NewGuid(),
            Name = "Drones",
            Slug = "drones"
        };

        DateTime t = new(2026, 10, 5, 8, 30, 0, DateTimeKind.Utc);
        category.Touch(t);

        Assert.NotNull(category.UpdatedAtUtc);
        Assert.Equal(t, category.UpdatedAtUtc.Value);
        Assert.Equal(DateTimeKind.Utc, category.UpdatedAtUtc.Value.Kind);
    }
}
