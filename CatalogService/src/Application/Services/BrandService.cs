using Dprental.CatalogService.Application.Clients;
using Dprental.CatalogService.Application.Common;
using Dprental.CatalogService.Application.DTOs.Brands;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Application.Validators;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.Extensions.Logging;

namespace Dprental.CatalogService.Application.Services;

/// <summary>
/// Triển khai nghiệp vụ cho Brand trong CatalogService.
/// Lưu ý kiến trúc: Để tránh giữ transaction DB trong khi gọi external HTTP MediaService,
/// media được verify và chuyển sang public trước khi persist Brand vào database.
/// Nếu database persist thất bại sau khi đã public media, media asset vẫn giữ trạng thái public
public sealed partial class BrandService(
    IBrandRepository repository,
    IMediaServiceClient mediaServiceClient,
    BrandListQueryValidator listQueryValidator,
    CreateBrandRequestValidator createValidator,
    UpdateBrandRequestValidator updateValidator,
    ILogger<BrandService> logger) : IBrandService
{
    public async Task<Result<PagedResult<BrandDto>>> SearchPublicAsync(
        GetBrandsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationResult = listQueryValidator.Validate(request.Page, request.PageSize, request.Keyword, request.Sort);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        string? codeKeyword = string.IsNullOrWhiteSpace(request.Keyword) ? null : request.Keyword.Trim();
        string? nameKeyword = string.IsNullOrWhiteSpace(request.Keyword) ? null : BrandValidationRules.NormalizeName(request.Keyword);

        var query = new BrandSearchQuery
        {
            Keyword = codeKeyword,
            NameKeyword = nameKeyword,
            IsActive = true, // Public list luôn chỉ lấy Brand đang active
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort
        };

        var pagedEntities = await repository.SearchAsync(query, cancellationToken);
        var dtos = pagedEntities.Items.Select(BrandDto.FromEntity).ToList();

        return new PagedResult<BrandDto>(dtos, pagedEntities.Page, pagedEntities.PageSize, pagedEntities.TotalCount);
    }

    public async Task<Result<PagedResult<BrandDto>>> SearchAdminAsync(
        GetAdminBrandsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationResult = listQueryValidator.Validate(request.Page, request.PageSize, request.Keyword, request.Sort);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        string? codeKeyword = string.IsNullOrWhiteSpace(request.Keyword) ? null : request.Keyword.Trim();
        string? nameKeyword = string.IsNullOrWhiteSpace(request.Keyword) ? null : BrandValidationRules.NormalizeName(request.Keyword);

        var query = new BrandSearchQuery
        {
            Keyword = codeKeyword,
            NameKeyword = nameKeyword,
            IsActive = request.IsActive, // Admin list hỗ trợ lọc isActive hoặc null (lấy cả hai)
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort
        };

        var pagedEntities = await repository.SearchAsync(query, cancellationToken);
        var dtos = pagedEntities.Items.Select(BrandDto.FromEntity).ToList();

        return new PagedResult<BrandDto>(dtos, pagedEntities.Page, pagedEntities.PageSize, pagedEntities.TotalCount);
    }

    public async Task<Result<BrandDto>> GetPublicByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Brand.InvalidId;
        }

        var brand = await repository.GetActiveByIdAsync(id, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.Brand.NotFound;
        }

        return BrandDto.FromEntity(brand);
    }

    public async Task<Result<BrandDto>> GetAdminByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Brand.InvalidId;
        }

        var brand = await repository.GetByIdAsync(id, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.Brand.AdminNotFound;
        }

        return BrandDto.FromEntity(brand);
    }

    public async Task<Result<BrandDto>> CreateAsync(
        CreateBrandRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationResult = createValidator.Validate(request, out var normalizedCode, out var normalizedName, out var normalizedSlug);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        // Kiểm tra trùng Code và Slug trước khi gọi MediaService
        if (await repository.IsCodeExistsAsync(normalizedCode, cancellationToken))
        {
            return CatalogErrors.Brand.DuplicateCode;
        }

        if (await repository.IsSlugExistsAsync(normalizedSlug, cancellationToken))
        {
            return CatalogErrors.Brand.DuplicateSlug;
        }

        // Nếu có logo, xác minh và chuyển visibility sang public trước khi lưu DB
        if (request.LogoMediaId.HasValue)
        {
            var mediaResult = await MediaValidationHelper.ValidateAndPublishMediaAsync(
                mediaServiceClient,
                request.LogoMediaId.Value,
                operatorUserId,
                cancellationToken);

            if (mediaResult.IsFailure)
            {
                return mediaResult.Error;
            }
        }

        var brand = new Brand
        {
            Id = Guid.NewGuid(),
            Code = normalizedCode,
            Name = normalizedName,
            Slug = normalizedSlug,
            LogoMediaId = request.LogoMediaId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = null
        };

        var saveResult = await repository.AddAsync(brand, cancellationToken);
        if (saveResult.IsFailure)
        {
            LogSaveBrandFailed(logger, normalizedCode, saveResult.Error.Message);
            return saveResult.Error;
        }

        return BrandDto.FromEntity(saveResult.Value);
    }

    public async Task<Result<BrandDto>> UpdateAsync(
        Guid id,
        UpdateBrandRequest request,
        string operatorUserId = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (id == Guid.Empty)
        {
            return CatalogErrors.Brand.InvalidId;
        }

        var validationResult = updateValidator.Validate(request, out var normalizedCode, out var normalizedName, out var normalizedSlug);
        if (validationResult.IsFailure)
        {
            return validationResult.Error;
        }

        var brand = await repository.GetByIdAsync(id, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.Brand.NotFound;
        }

        // Kiểm tra duplicate Code nếu có cập nhật và thay đổi giá trị
        if (request.IsCodeSpecified && !string.Equals(brand.Code, normalizedCode, StringComparison.OrdinalIgnoreCase))
        {
            if (await repository.IsCodeExistsAsync(normalizedCode!, id, cancellationToken))
            {
                return CatalogErrors.Brand.DuplicateCode;
            }
        }

        // Kiểm tra duplicate Slug nếu có cập nhật và thay đổi giá trị
        if (request.IsSlugSpecified && !string.Equals(brand.Slug, normalizedSlug, StringComparison.OrdinalIgnoreCase))
        {
            if (await repository.IsSlugExistsAsync(normalizedSlug!, id, cancellationToken))
            {
                return CatalogErrors.Brand.DuplicateSlug;
            }
        }

        // Xử lý logo: omitted giữ nguyên; null gỡ bỏ; giữ nguyên giá trị cũ không verify lại; giá trị mới verify và public
        if (request.IsLogoMediaIdSpecified)
        {
            if (!request.LogoMediaId.HasValue)
            {
                brand.LogoMediaId = null;
            }
            else if (brand.LogoMediaId != request.LogoMediaId.Value)
            {
                var mediaResult = await MediaValidationHelper.ValidateAndPublishMediaAsync(
                    mediaServiceClient,
                    request.LogoMediaId.Value,
                    operatorUserId,
                    cancellationToken);

                if (mediaResult.IsFailure)
                {
                    return mediaResult.Error;
                }

                brand.LogoMediaId = request.LogoMediaId.Value;
            }
        }

        if (request.IsCodeSpecified)
        {
            brand.Code = normalizedCode!;
        }

        if (request.IsNameSpecified)
        {
            brand.Name = normalizedName!;
        }

        if (request.IsSlugSpecified)
        {
            brand.Slug = normalizedSlug!;
        }

        brand.Touch(DateTime.UtcNow);

        var updateResult = await repository.UpdateAsync(brand, cancellationToken);
        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        return BrandDto.FromEntity(updateResult.Value);
    }

    public async Task<Result<BrandDto>> DisableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Brand.InvalidId;
        }

        var brand = await repository.GetByIdAsync(id, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.Brand.NotFound;
        }

        // Idempotent: nếu đã inactive thì không thay đổi UpdatedAt và không gọi DB update
        if (!brand.Disable(DateTime.UtcNow))
        {
            return BrandDto.FromEntity(brand);
        }

        var updateResult = await repository.UpdateAsync(brand, cancellationToken);
        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        return BrandDto.FromEntity(updateResult.Value);
    }

    public async Task<Result<BrandDto>> EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            return CatalogErrors.Brand.InvalidId;
        }

        var brand = await repository.GetByIdAsync(id, cancellationToken);
        if (brand is null)
        {
            return CatalogErrors.Brand.NotFound;
        }

        // Idempotent: nếu đã active thì không thay đổi UpdatedAt và không gọi DB update
        if (!brand.Enable(DateTime.UtcNow))
        {
            return BrandDto.FromEntity(brand);
        }

        var updateResult = await repository.UpdateAsync(brand, cancellationToken);
        if (updateResult.IsFailure)
        {
            return updateResult.Error;
        }

        return BrandDto.FromEntity(updateResult.Value);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Không thể lưu Brand {Code} vào database sau khi kiểm tra media: {Error}")]
    private static partial void LogSaveBrandFailed(ILogger logger, string code, string error);
}
