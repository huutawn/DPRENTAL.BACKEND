using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

/// <summary>
/// Bản tạm của <see cref="ICatalogReferenceClient"/>: Rental chưa có hợp đồng nào với CatalogService.
/// Luôn báo dịch vụ chưa sẵn sàng. Thay bằng adapter thật khi có hợp đồng.
/// </summary>
public sealed class UnavailableCatalogReferenceClient : ICatalogReferenceClient
{
    private static readonly Error ContractPending = Error.ServiceUnavailable(
        "Catalog.ReferenceContractPending", "Chưa có hợp đồng tra tên loại thiết bị và hãng cho checkout.");

    public Task<Result<string?>> GetEquipmentTypeNameAsync(Guid equipmentTypeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<string?>(ContractPending));

    public Task<Result<string?>> GetBrandNameAsync(Guid brandId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure<string?>(ContractPending));
}
