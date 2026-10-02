using Dprental.CatalogService.Application.DTOs.EquipmentType;
using Dprental.CatalogService.Application.IServices;
using Dprental.CatalogService.Common;
using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;

namespace Dprental.CatalogService.Application.Services;

/// <summary>Đọc công khai equipment type: chỉ thấy type Published, không bao giờ thấy Draft hay Disabled.</summary>
public sealed class EquipmentTypeCatalogService(IEquipmentTypeRepository equipmentTypeRepository) : IEquipmentTypeCatalogService
{
    public async Task<IReadOnlyList<EquipmentTypeResponse>> ListPublishedAsync(
        Guid? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<EquipmentType> types = await equipmentTypeRepository.ListPublishedAsync(categoryId, cancellationToken);
        return types.Select(ToResponse).ToList();
    }

    public async Task<Result<EquipmentTypeResponse>> GetPublishedAsync(
        Guid equipmentTypeId,
        CancellationToken cancellationToken = default)
    {
        EquipmentType? equipmentType = await equipmentTypeRepository.GetPublishedAsync(equipmentTypeId, cancellationToken);
        return equipmentType is null ? CatalogErrors.EquipmentType.NotFound : ToResponse(equipmentType);
    }

    private static EquipmentTypeResponse ToResponse(EquipmentType equipmentType) =>
        new(
            equipmentType.Id,
            equipmentType.CategoryId,
            equipmentType.Category.Name,
            equipmentType.Code,
            equipmentType.Name,
            equipmentType.Description,
            equipmentType.RequiresSpecSchema);
}
