using System.Text.RegularExpressions;
using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;

namespace Dprental.CatalogService.Application.Services;

/// <summary>
/// Điều phối vòng đời equipment type của Canonical Catalog.
/// Không xử lý organization, giá hay tình trạng sẵn có: các dữ liệu đó thuộc Supplier Asset.
/// </summary>
public sealed partial class EquipmentTypeManagementService(
    IEquipmentTypeRepository equipmentTypeRepository,
    ISpecSchemaCatalog specSchemaCatalog,
    TimeProvider timeProvider) : IEquipmentTypeManagementService
{
    public async Task<Result<EquipmentTypeManagementResponse>> CreateAsync(
        CreateEquipmentTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<string> code = ReadCode(request.Code);
        if (code.IsFailure)
        {
            return code.Error;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return CatalogErrors.EquipmentType.NameRequired;
        }

        Result<Category> category = await GetActiveCategoryAsync(request.CategoryId ?? Guid.Empty, cancellationToken);
        if (category.IsFailure)
        {
            return category.Error;
        }

        // Status không được gán ở đây: equipment type mới luôn là Draft.
        EquipmentType equipmentType = new()
        {
            Id = Guid.CreateVersion7(),
            CategoryId = category.Value.Id,
            Category = category.Value,
            Code = code.Value,
            Description = NullIfBlank(request.Description),
            RequiresSpecSchema = request.RequiresSpecSchema ?? false,
            CreatedAtUtc = UtcNow()
        };
        equipmentType.Rename(request.Name);

        Error? duplicate = await FindDuplicateAsync(equipmentType, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        await equipmentTypeRepository.AddAsync(equipmentType, cancellationToken);

        return await SaveAsync(equipmentType, cancellationToken);
    }

    public async Task<Result<EquipmentTypeManagementResponse>> UpdateAsync(
        Guid equipmentTypeId,
        UpdateEquipmentTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EquipmentType? equipmentType = await equipmentTypeRepository.GetForUpdateAsync(equipmentTypeId, cancellationToken);
        if (equipmentType is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        if (request.Code is not null)
        {
            Result<string> code = ReadCode(request.Code);
            if (code.IsFailure)
            {
                return code.Error;
            }

            if (!string.Equals(code.Value, equipmentType.Code, StringComparison.Ordinal))
            {
                // Type đã từng publish có thể đã được Inventory tham chiếu theo mã.
                if (equipmentType.IsCodeLocked)
                {
                    return CatalogErrors.EquipmentType.CodeLocked;
                }

                equipmentType.Code = code.Value;
            }
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return CatalogErrors.EquipmentType.NameRequired;
            }

            equipmentType.Rename(request.Name);
        }

        if (request.Description is not null)
        {
            equipmentType.Description = NullIfBlank(request.Description);
        }

        if (request.RequiresSpecSchema is not null)
        {
            equipmentType.RequiresSpecSchema = request.RequiresSpecSchema.Value;
        }

        if (request.CategoryId is not null && request.CategoryId != equipmentType.CategoryId)
        {
            Result<Category> category = await GetActiveCategoryAsync(request.CategoryId.Value, cancellationToken);
            if (category.IsFailure)
            {
                return category.Error;
            }

            equipmentType.CategoryId = category.Value.Id;
            equipmentType.Category = category.Value;
        }

        Error? duplicate = await FindDuplicateAsync(equipmentType, cancellationToken);
        if (duplicate is not null)
        {
            return duplicate;
        }

        // Type đang công khai không được sửa thành trạng thái không còn đủ điều kiện publish.
        if (equipmentType.Status == EquipmentTypeStatus.Published)
        {
            List<string> blockers = await GetPublishBlockersAsync(equipmentType, cancellationToken);
            if (blockers.Count > 0)
            {
                return CatalogErrors.EquipmentType.NotPublishable(blockers);
            }
        }

        equipmentType.UpdatedAtUtc = UtcNow();

        return await SaveAsync(equipmentType, cancellationToken);
    }

    public async Task<Result<EquipmentTypeManagementResponse>> PublishAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        EquipmentType? equipmentType = await equipmentTypeRepository.GetForUpdateAsync(equipmentTypeId, cancellationToken);
        if (equipmentType is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        if (equipmentType.Status == EquipmentTypeStatus.Published)
        {
            return CatalogErrors.EquipmentType.AlreadyPublished;
        }

        List<string> blockers = await GetPublishBlockersAsync(equipmentType, cancellationToken);
        if (blockers.Count > 0)
        {
            return CatalogErrors.EquipmentType.NotPublishable(blockers);
        }

        equipmentType.Publish(UtcNow());

        return await SaveAsync(equipmentType, cancellationToken);
    }

    public async Task<Result<EquipmentTypeManagementResponse>> DisableAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        EquipmentType? equipmentType = await equipmentTypeRepository.GetForUpdateAsync(equipmentTypeId, cancellationToken);
        if (equipmentType is null)
        {
            return CatalogErrors.EquipmentType.NotFound;
        }

        if (equipmentType.Status != EquipmentTypeStatus.Published)
        {
            return CatalogErrors.EquipmentType.NotPublished;
        }

        // Disable giữ nguyên dữ liệu, nên các tham chiếu từ Inventory vẫn hợp lệ.
        equipmentType.Disable(UtcNow());

        return await SaveAsync(equipmentType, cancellationToken);
    }

    public async Task<Result<EquipmentTypeManagementResponse>> GetForManagementAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        EquipmentType? equipmentType = await equipmentTypeRepository.GetForUpdateAsync(equipmentTypeId, cancellationToken);
        return equipmentType is null ? CatalogErrors.EquipmentType.NotFound : ToResponse(equipmentType);
    }

    /// <summary>Các điều kiện publish chưa đạt; danh sách rỗng nghĩa là publish được.</summary>
    private async Task<List<string>> GetPublishBlockersAsync(EquipmentType equipmentType, CancellationToken cancellationToken)
    {
        List<string> blockers = [];

        if (!equipmentType.Category.IsActive)
        {
            blockers.Add("category đã ngừng hoạt động");
        }

        if (!CodePattern().IsMatch(equipmentType.Code))
        {
            blockers.Add("mã không hợp lệ");
        }

        if (string.IsNullOrWhiteSpace(equipmentType.Name))
        {
            blockers.Add("thiếu tên");
        }

        if (await FindDuplicateAsync(equipmentType, cancellationToken) is not null)
        {
            blockers.Add("trùng với một equipment type khác");
        }

        if (equipmentType.RequiresSpecSchema
            && !await specSchemaCatalog.HasPublishedSchemaAsync(equipmentType.Id, cancellationToken))
        {
            blockers.Add("type yêu cầu SpecSchema nhưng chưa có phiên bản SpecSchema nào được publish");
        }

        return blockers;
    }

    /// <summary>Hai type bị coi là trùng khi cùng mã, hoặc cùng tên (đã chuẩn hóa) trong một category.</summary>
    private async Task<Error?> FindDuplicateAsync(EquipmentType equipmentType, CancellationToken cancellationToken)
    {
        if (await equipmentTypeRepository.CodeExistsAsync(equipmentType.Code, equipmentType.Id, cancellationToken))
        {
            return CatalogErrors.EquipmentType.CodeAlreadyExists;
        }

        return await equipmentTypeRepository.NameExistsAsync(
            equipmentType.CategoryId,
            equipmentType.NormalizedName,
            equipmentType.Id,
            cancellationToken)
            ? CatalogErrors.EquipmentType.NameAlreadyExists
            : null;
    }

    /// <summary>Lưu và đổi xung đột giữa hai request chạy cùng lúc (trùng unique, cùng sửa một type) thành lỗi 409.</summary>
    private async Task<Result<EquipmentTypeManagementResponse>> SaveAsync(
        EquipmentType equipmentType,
        CancellationToken cancellationToken) =>
        await equipmentTypeRepository.SaveChangesAsync(cancellationToken) switch
        {
            EquipmentTypeSaveOutcome.DuplicateCode => CatalogErrors.EquipmentType.CodeAlreadyExists,
            EquipmentTypeSaveOutcome.DuplicateName => CatalogErrors.EquipmentType.NameAlreadyExists,
            EquipmentTypeSaveOutcome.ConcurrentUpdate => CatalogErrors.EquipmentType.ConcurrentUpdate,
            _ => ToResponse(equipmentType)
        };

    private async Task<Result<Category>> GetActiveCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        Category? category = await equipmentTypeRepository.GetCategoryAsync(categoryId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.Category.NotFound;
        }

        return category.IsActive ? category : CatalogErrors.Category.Inactive;
    }

    /// <summary>Mã luôn được lưu dạng viết hoa để so sánh không phân biệt hoa thường.</summary>
    private static Result<string> ReadCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return CatalogErrors.EquipmentType.CodeRequired;
        }

        string normalized = code.Trim().ToUpperInvariant();
        return CodePattern().IsMatch(normalized) ? normalized : CatalogErrors.EquipmentType.CodeInvalid;
    }

    private static EquipmentTypeManagementResponse ToResponse(EquipmentType equipmentType) =>
        new(
            equipmentType.Id,
            equipmentType.CategoryId,
            equipmentType.Code,
            equipmentType.Name,
            equipmentType.Description,
            equipmentType.RequiresSpecSchema,
            equipmentType.Status.ToString(),
            equipmentType.IsCodeLocked,
            equipmentType.CreatedAtUtc,
            equipmentType.UpdatedAtUtc,
            equipmentType.PublishedAtUtc,
            equipmentType.DisabledAtUtc);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Chữ cái không dấu viết hoa, chữ số, gạch ngang, gạch dưới, dấu chấm; ví dụ FORKLIFT-1.5-3.5.</summary>
    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]*$")]
    private static partial Regex CodePattern();
}
