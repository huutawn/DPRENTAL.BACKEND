using System.Net;
using System.Text.Json;
using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Application.DTOs.Media;
using Dprental.CatalogService.Application.Options;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Utils;
using Dprental.CatalogService.Domain.Entities;
using Microsoft.Extensions.Options;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class BrandUnitAndValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Theory]
    [InlineData("sony", "SONY")]
    [InlineData("  canon  ", "CANON")]
    [InlineData("BLACKMAGIC", "BLACKMAGIC")]
    [InlineData("a", "A")]
    public void CodeValidation_NormalizesToUppercaseAndTrim(string raw, string expected)
    {
        var result = BrandValidationRules.ValidateCode(raw, out var normalized);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CodeValidation_RejectsEmptyOrWhitespace(string? raw)
    {
        var result = BrandValidationRules.ValidateCode(raw, out var normalized);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidCode, result.Error.Code);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("SONY\0")]
    [InlineData("\0SONY")]
    [InlineData("SO\0NY")]
    public void CodeValidation_RejectsNulCharacter(string raw)
    {
        var result = BrandValidationRules.ValidateCode(raw, out var normalized);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidCode, result.Error.Code);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void CodeValidation_RejectsLengthExceeding50()
    {
        string oversized = new('A', 51);
        var result = BrandValidationRules.ValidateCode(oversized, out _);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidCode, result.Error.Code);
    }

    [Fact]
    public void CodeValidation_AcceptsLength50()
    {
        string maxCode = new('A', 50);
        var result = BrandValidationRules.ValidateCode(maxCode, out var normalized);

        Assert.True(result.IsSuccess);
        Assert.Equal(maxCode, normalized);
    }

    [Theory]
    [InlineData("  Sony   Corporation  ", "Sony Corporation")]
    [InlineData("Sony\t\nCorporation", "Sony Corporation")]
    [InlineData("Thiết   bị   DP", "Thiết bị DP")]
    public void NameValidation_NormalizesUnicodeNfcAndCollapsesWhitespace(string raw, string expected)
    {
        var result = BrandValidationRules.ValidateName(raw, out var normalized);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, normalized);
        Assert.True(normalized.IsNormalized(System.Text.NormalizationForm.FormC));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NameValidation_RejectsEmptyOrWhitespace(string? raw)
    {
        var result = BrandValidationRules.ValidateName(raw, out var normalized);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidName, result.Error.Code);
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void NameValidation_RejectsLengthExceeding200()
    {
        string oversized = new('A', 201);
        var result = BrandValidationRules.ValidateName(oversized, out _);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidName, result.Error.Code);
    }

    [Theory]
    [InlineData("Sony\0")]
    [InlineData("\0Sony")]
    [InlineData("Sony\0Corporation")]
    public void NameValidation_RejectsNulCharacter(string raw)
    {
        var result = BrandValidationRules.ValidateName(raw, out var normalized);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidName, result.Error.Code);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("sony\0")]
    [InlineData("\0sony")]
    [InlineData("sony\0alpha")]
    public void SlugValidation_RejectsNulCharacter(string raw)
    {
        var result = BrandValidationRules.ValidateSlug(raw, out var normalized);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidSlug, result.Error.Code);
        Assert.Equal(string.Empty, normalized);
    }

    [Theory]
    [InlineData("sony", "sony")]
    [InlineData("  SONY-ALPHA  ", "sony-alpha")]
    [InlineData("canon-eos-r5", "canon-eos-r5")]
    [InlineData("blackmagic-design-6k", "blackmagic-design-6k")]
    [InlineData("123-abc", "123-abc")]
    [InlineData("a", "a")]
    [InlineData("1", "1")]
    public void SlugValidation_AcceptsValidAsciiAlphanumericSeparatedByHyphens(string raw, string expected)
    {
        var result = BrandValidationRules.ValidateSlug(raw, out var normalized);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-sony")]
    [InlineData("sony-")]
    [InlineData("sony--alpha")]
    [InlineData("sony_alpha")]
    [InlineData("sony alpha")]
    [InlineData("sôny")]
    [InlineData("sony.alpha")]
    [InlineData("sony@alpha")]
    public void SlugValidation_RejectsInvalidFormatOrCharacters(string? raw)
    {
        var result = BrandValidationRules.ValidateSlug(raw, out _);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidSlug, result.Error.Code);
    }

    [Fact]
    public void SlugValidation_RejectsLengthExceeding150()
    {
        string oversized = new('a', 151);
        var result = BrandValidationRules.ValidateSlug(oversized, out _);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidSlug, result.Error.Code);
    }

    [Fact]
    public void LogoMediaIdValidation_RejectsEmptyGuid()
    {
        var result = BrandValidationRules.ValidateLogoMediaId(Guid.Empty, isSpecified: true);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidLogo, result.Error.Code);
    }

    [Fact]
    public void LogoMediaIdValidation_AcceptsNullOrValidGuid()
    {
        var nullResult = BrandValidationRules.ValidateLogoMediaId(null, isSpecified: true);
        Assert.True(nullResult.IsSuccess);

        var validResult = BrandValidationRules.ValidateLogoMediaId(Guid.NewGuid(), isSpecified: true);
        Assert.True(validResult.IsSuccess);
    }

    [Fact]
    public void CreateValidator_ValidPayload_ReturnsNormalizedValues()
    {
        var validator = new CreateBrandRequestValidator();
        var request = new CreateBrandRequest
        {
            Code = "  sony  ",
            Name = "  Sony   Electronics  ",
            Slug = "  SONY-ELECTRONICS  ",
            LogoMediaId = Guid.NewGuid()
        };

        var result = validator.Validate(request, out var code, out var name, out var slug);

        Assert.True(result.IsSuccess);
        Assert.Equal("SONY", code);
        Assert.Equal("Sony Electronics", name);
        Assert.Equal("sony-electronics", slug);
    }

    [Fact]
    public void CreateValidator_MissingField_FailsWithCorrectError()
    {
        var validator = new CreateBrandRequestValidator();

        var missingCode = new CreateBrandRequest { Code = "", Name = "Sony", Slug = "sony" };
        var codeRes = validator.Validate(missingCode, out _, out _, out _);
        Assert.Equal(CatalogErrorCode.Brand.InvalidCode, codeRes.Error.Code);

        var missingName = new CreateBrandRequest { Code = "SONY", Name = "", Slug = "sony" };
        var nameRes = validator.Validate(missingName, out _, out _, out _);
        Assert.Equal(CatalogErrorCode.Brand.InvalidName, nameRes.Error.Code);

        var invalidSlug = new CreateBrandRequest { Code = "SONY", Name = "Sony", Slug = "invalid_slug" };
        var slugRes = validator.Validate(invalidSlug, out _, out _, out _);
        Assert.Equal(CatalogErrorCode.Brand.InvalidSlug, slugRes.Error.Code);

        var emptyLogo = new CreateBrandRequest { Code = "SONY", Name = "Sony", Slug = "sony", LogoMediaId = Guid.Empty };
        var logoRes = validator.Validate(emptyLogo, out _, out _, out _);
        Assert.Equal(CatalogErrorCode.Brand.InvalidLogo, logoRes.Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateValidator_OmittedOrBlankSlug_SucceedsWithEmptySlugForAutoGeneration(string? rawSlug)
    {
        var validator = new CreateBrandRequestValidator();
        var request = new CreateBrandRequest { Code = "SONY", Name = "Sony", Slug = rawSlug! };

        var result = validator.Validate(request, out _, out _, out var slug);

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, slug);
    }

    [Theory]
    [InlineData("Sony", "sony")]
    [InlineData("  Sony   Điện Tử  ", "sony-dien-tu")]
    [InlineData("Máy Ảnh Đức", "may-anh-duc")]
    [InlineData("A - B", "a-b")]
    [InlineData("-Sony-", "sony")]
    [InlineData("Black & Decker", "black-decker")]
    [InlineData("sony--alpha", "sony-alpha")]
    [InlineData("!!!", "")]
    [InlineData("日本", "")]
    public void SlugHelper_GeneratesSlugMatchingBrandSlugFormat(string phrase, string expected)
    {
        string slug = SlugHelper.GenerateSlug(phrase, BrandValidationRules.MaxSlugLength);

        Assert.Equal(expected, slug);
        if (expected.Length > 0)
        {
            Assert.True(BrandValidationRules.ValidateSlug(slug, out _).IsSuccess);
        }
    }

    [Fact]
    public void SlugHelper_TruncatesToMaxLength_WithoutTrailingHyphen()
    {
        string phrase = new string('a', 149) + " " + new string('b', 20);

        string slug = SlugHelper.GenerateSlug(phrase, BrandValidationRules.MaxSlugLength);

        Assert.Equal(new string('a', 149), slug);
        Assert.True(BrandValidationRules.ValidateSlug(slug, out _).IsSuccess);
    }

    [Fact]
    public void UpdateValidator_EmptyPayload_FailsWithInvalidPayload()
    {
        var validator = new UpdateBrandRequestValidator();
        var emptyReq = new UpdateBrandRequest();

        var result = validator.Validate(emptyReq, out _, out _, out _);

        Assert.True(result.IsFailure);
        Assert.Equal(CatalogErrorCode.Brand.InvalidPayload, result.Error.Code);
    }

    [Fact]
    public void UpdateValidator_OmittedFieldsRemainNull_SpecifiedFieldsNormalized()
    {
        var validator = new UpdateBrandRequestValidator();
        var updateReq = new UpdateBrandRequest
        {
            IsNameSpecified = true,
            Name = "  Sony   Updated  "
        };

        var result = validator.Validate(updateReq, out var code, out var name, out var slug);

        Assert.True(result.IsSuccess);
        Assert.Null(code);
        Assert.Equal("Sony Updated", name);
        Assert.Null(slug);
    }

    [Fact]
    public void UpdateValidator_ExplicitNullCodeNameSlug_FailsValidation()
    {
        var validator = new UpdateBrandRequestValidator();

        var nullCode = new UpdateBrandRequest { IsCodeSpecified = true, Code = null };
        Assert.Equal(CatalogErrorCode.Brand.InvalidCode, validator.Validate(nullCode, out _, out _, out _).Error.Code);

        var nullName = new UpdateBrandRequest { IsNameSpecified = true, Name = null };
        Assert.Equal(CatalogErrorCode.Brand.InvalidName, validator.Validate(nullName, out _, out _, out _).Error.Code);

        var nullSlug = new UpdateBrandRequest { IsSlugSpecified = true, Slug = null };
        Assert.Equal(CatalogErrorCode.Brand.InvalidSlug, validator.Validate(nullSlug, out _, out _, out _).Error.Code);
    }

    [Fact]
    public void UpdateValidator_ExplicitNullLogo_IsAllowedToClearLogo()
    {
        var validator = new UpdateBrandRequestValidator();
        var clearLogo = new UpdateBrandRequest { IsLogoMediaIdSpecified = true, LogoMediaId = null };

        var result = validator.Validate(clearLogo, out _, out _, out _);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void BrandListQueryValidator_PagingRulesEnforced()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new CatalogDiscoveryOptions { MaxKeywordLength = 100 });
        var validator = new BrandListQueryValidator(options);

        // Page <= 0
        Assert.Equal(CatalogErrorCode.Paging.InvalidPage, validator.Validate(0, 20, null, null).Error.Code);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPage, validator.Validate(-1, 20, null, null).Error.Code);

        // PageSize <= 0 or > 100
        Assert.Equal(CatalogErrorCode.Paging.InvalidPageSize, validator.Validate(1, 0, null, null).Error.Code);
        Assert.Equal(CatalogErrorCode.Paging.InvalidPageSize, validator.Validate(1, 101, null, null).Error.Code);

        // Overflow
        Assert.Equal(CatalogErrorCode.Paging.Overflow, validator.Validate(int.MaxValue, 100, null, null).Error.Code);

        // Keyword length
        Assert.Equal(CatalogErrorCode.Brand.InvalidKeyword, validator.Validate(1, 20, new string('a', 101), null).Error.Code);
        // NUL character in keyword
        Assert.Equal(CatalogErrorCode.Brand.InvalidKeyword, validator.Validate(1, 20, "sony\0", null).Error.Code);
        Assert.Equal(CatalogErrorCode.Brand.InvalidKeyword, validator.Validate(1, 20, "\0", null).Error.Code);

        // Invalid sort
        Assert.Equal(CatalogErrorCode.Brand.InvalidSort, validator.Validate(1, 20, null, "invalid_sort").Error.Code);

        // Valid sorts
        Assert.True(validator.Validate(1, 20, null, "newest").IsSuccess);
        Assert.True(validator.Validate(1, 20, null, "name_asc").IsSuccess);
        Assert.True(validator.Validate(1, 20, null, "name_desc").IsSuccess);
        Assert.True(validator.Validate(1, 20, null, null).IsSuccess);
    }

    [Theory]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "organizationId": "11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "isActive": true}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "extra": "field"}""")]
    public void CreateBrandRequest_RejectsUnknownProperties(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateBrandRequest>(json, JsonOptions));
    }

    [Theory]
    [InlineData("""{"name": "Sony", "organizationId": "11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"name": "Sony", "isActive": true}""")]
    [InlineData("""{"name": "Sony", "extra": "field"}""")]
    public void UpdateBrandRequest_RejectsUnknownProperties(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateBrandRequest>(json, JsonOptions));
    }

    [Theory]
    [InlineData("""{"code": 123, "name": "Sony", "slug": "sony"}""")]
    [InlineData("""{"code": "SONY", "name": true, "slug": "sony"}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": 456}""")]
    [InlineData("""{"code": "SONY", "name": "Sony", "slug": "sony", "logoMediaId": "not-a-guid"}""")]
    public void CreateBrandRequest_RejectsInvalidPropertyTypes(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateBrandRequest>(json, JsonOptions));
    }

    [Theory]
    [InlineData("""{"code": 123}""")]
    [InlineData("""{"name": true}""")]
    [InlineData("""{"slug": 456}""")]
    [InlineData("""{"logoMediaId": "not-a-guid"}""")]
    public void UpdateBrandRequest_RejectsInvalidPropertyTypes(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateBrandRequest>(json, JsonOptions));
    }

    [Fact]
    public void UpdateBrandRequest_DistinguishesOmittedVsNullLogo()
    {
        string omittedJson = """{"name": "New Name"}""";
        var omittedReq = JsonSerializer.Deserialize<UpdateBrandRequest>(omittedJson, JsonOptions);
        Assert.NotNull(omittedReq);
        Assert.False(omittedReq.IsLogoMediaIdSpecified);
        Assert.Null(omittedReq.LogoMediaId);

        string nullJson = """{"logoMediaId": null}""";
        var nullReq = JsonSerializer.Deserialize<UpdateBrandRequest>(nullJson, JsonOptions);
        Assert.NotNull(nullReq);
        Assert.True(nullReq.IsLogoMediaIdSpecified);
        Assert.Null(nullReq.LogoMediaId);

        Guid logoId = Guid.NewGuid();
        string valJson = $$"""{"logoMediaId": "{{logoId}}"}""";
        var valReq = JsonSerializer.Deserialize<UpdateBrandRequest>(valJson, JsonOptions);
        Assert.NotNull(valReq);
        Assert.True(valReq.IsLogoMediaIdSpecified);
        Assert.Equal(logoId, valReq.LogoMediaId);
    }

    [Fact]
    public void BrandEntity_DisableAndEnable_AreIdempotent()
    {
        var brand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = "SONY",
            Name = "Sony",
            Slug = "sony",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        DateTime t1 = new(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
        DateTime t2 = new(2026, 10, 5, 11, 0, 0, DateTimeKind.Utc);

        // 1. Disable active brand: changes state, updates UpdatedAt
        bool changed1 = brand.Disable(t1);
        Assert.True(changed1);
        Assert.False(brand.IsActive);
        Assert.Equal(t1, brand.UpdatedAt);

        // 2. Disable already inactive brand: no change, UpdatedAt remains t1
        bool changed2 = brand.Disable(t2);
        Assert.False(changed2);
        Assert.False(brand.IsActive);
        Assert.Equal(t1, brand.UpdatedAt);

        // 3. Enable inactive brand: changes state, updates UpdatedAt to t2
        bool changed3 = brand.Enable(t2);
        Assert.True(changed3);
        Assert.True(brand.IsActive);
        Assert.Equal(t2, brand.UpdatedAt);

        // 4. Enable already active brand: no change, UpdatedAt remains t2
        DateTime t3 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        bool changed4 = brand.Enable(t3);
        Assert.False(changed4);
        Assert.True(brand.IsActive);
        Assert.Equal(t2, brand.UpdatedAt);
    }

    [Fact]
    public async Task MediaValidationHelper_EnforcesRules()
    {
        var mediaClient = new DummyTestMediaClient();
        Guid mediaId = Guid.NewGuid();

        // 1. Not ready -> Media.NotReady
        mediaClient.Setup(mediaId, status: "Uploading", contentType: "image/jpeg", uploader: "user1");
        var res1 = await MediaValidationHelper.ValidateAndPublishMediaAsync(mediaClient, mediaId, "user1");
        Assert.Equal(CatalogErrorCode.Media.NotReady, res1.Error.Code);

        // 2. Invalid MIME -> Media.InvalidContentType
        mediaClient.Setup(mediaId, status: "Ready", contentType: "application/pdf", uploader: "user1");
        var res2 = await MediaValidationHelper.ValidateAndPublishMediaAsync(mediaClient, mediaId, "user1");
        Assert.Equal(CatalogErrorCode.Media.InvalidContentType, res2.Error.Code);

        // 3. Different uploader -> Media.Forbidden
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/png", uploader: "other-user");
        var res3 = await MediaValidationHelper.ValidateAndPublishMediaAsync(mediaClient, mediaId, "user1");
        Assert.Equal(CatalogErrorCode.Media.Forbidden, res3.Error.Code);

        // 4. Valid media -> SetVisibility called and succeeds
        mediaClient.Setup(mediaId, status: "Ready", contentType: "image/webp", uploader: "user1");
        var res4 = await MediaValidationHelper.ValidateAndPublishMediaAsync(mediaClient, mediaId, "user1");
        Assert.True(res4.IsSuccess);
        Assert.Equal("public", mediaClient.GetVisibility(mediaId));
    }

    private sealed class DummyTestMediaClient : IMediaServiceClient
    {
        private readonly Dictionary<Guid, (string Status, string ContentType, string Uploader)> _media = [];
        private readonly Dictionary<Guid, string> _visibility = [];

        public void Setup(Guid id, string status, string contentType, string uploader)
        {
            _media[id] = (status, contentType, uploader);
        }

        public string? GetVisibility(Guid id) => _visibility.GetValueOrDefault(id);

        public Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
        {
            if (!_media.TryGetValue(mediaId, out var m))
            {
                return Task.FromResult(Result.Failure<MediaVerificationDto>(CatalogErrors.Media.NotFound));
            }

            return Task.FromResult(Result.Success(new MediaVerificationDto(mediaId, m.Status, m.Uploader, "test.png", m.ContentType, 1024, DateTimeOffset.UtcNow)));
        }

        public Task<Result> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default)
        {
            _visibility[mediaId] = visibility;
            return Task.FromResult(Result.Success());
        }
    }
}
