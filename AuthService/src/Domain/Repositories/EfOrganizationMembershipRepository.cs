using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfOrganizationMembershipRepository(AuthDbContext dbContext) : IOrganizationMembershipRepository
{
    public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, cancellationToken);

    public async Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && (m.Role.Name == "Owner" || m.Role.Name == "owner") && m.Status == MembershipStatus.Active)
            .CountAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetScopedPermissionCodesAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.OrganizationId == organizationId && m.Status == MembershipStatus.Active)
            .SelectMany(m => m.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OrganizationMembership>> GetMembershipsForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Include(m => m.Organization)
            .Include(m => m.Role)
            .Where(m => m.UserId == userId)
            .ToListAsync(cancellationToken);
}
