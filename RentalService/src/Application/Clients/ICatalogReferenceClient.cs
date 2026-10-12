using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Cổng tra tên EquipmentType và Brand từ CatalogService để chụp snapshot.
/// Result thất bại nghĩa là không gọi được Catalog; giá trị <c>null</c> nghĩa là Catalog không còn công khai mục đó.
/// </summary>
public interface ICatalogReferenceClient
{
    Task<Result<string?>> GetEquipmentTypeNameAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default);

    Task<Result<string?>> GetBrandNameAsync(Guid brandId, CancellationToken cancellationToken = default);
}
