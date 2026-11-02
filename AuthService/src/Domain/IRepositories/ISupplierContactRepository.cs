using Dprental.AuthService.Common.Enums;

namespace Dprental.AuthService.Domain.IRepositories;

public sealed record SupplierContactRecord(string Name, string? ContactPhone, OrganizationType Type, OrganizationStatus Status);

public interface ISupplierContactRepository
{
    Task<SupplierContactRecord?> FindAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
