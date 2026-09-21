using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfOrganizationMembershipRepository(AuthDbContext dbContext) : IOrganizationMembershipRepository
{
    public Task<OrganizationMembership?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<OrganizationMembership?> GetByOrganizationAndUserAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, cancellationToken);

    public Task<OrganizationMembership?> FindMemberAsync(Guid organizationId, Guid memberOrUserId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .SingleOrDefaultAsync(m => m.OrganizationId == organizationId && (m.Id == memberOrUserId || m.UserId == memberOrUserId), cancellationToken);

    public async Task<IReadOnlyList<OrganizationMembership>> GetMembersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Include(m => m.User)
            .Include(m => m.Role)
            .Where(m => m.OrganizationId == organizationId)
            .OrderBy(m => m.JoinedAtUtc)
            .ToListAsync(cancellationToken);

    public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, cancellationToken);

    public async Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);

        if (await MembershipExistsAsync(membership.OrganizationId, membership.UserId, cancellationToken))
        {
            throw new DuplicateMembershipException("User đã là thành viên của organization này.");
        }

        dbContext.OrganizationMemberships.Add(membership);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return membership;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(membership).State = EntityState.Detached;
            throw new DuplicateMembershipException("User đã là thành viên của organization này.", exception);
        }
    }

    public async Task<bool> UpdateAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);

        OrganizationMembership? existing = await dbContext.OrganizationMemberships
            .FindAsync([membership.Id], cancellationToken);

        if (existing is null)
        {
            return false;
        }

        existing.RoleId = membership.RoleId;
        existing.Status = membership.Status;
        existing.JoinedAtUtc = membership.JoinedAtUtc;

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid membershipId, CancellationToken cancellationToken = default)
    {
        OrganizationMembership? membership = await dbContext.OrganizationMemberships.FindAsync([membershipId], cancellationToken);
        if (membership is null)
        {
            return false;
        }

        dbContext.OrganizationMemberships.Remove(membership);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> CountOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && (m.Role.Name == "Owner" || m.Role.Name == "owner"))
            .CountAsync(cancellationToken);

    public async Task<int> CountActiveOwnersAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId && (m.Role.Name == "Owner" || m.Role.Name == "owner") && m.Status == MembershipStatus.Active)
            .CountAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        List<string> globalPermissions = await dbContext.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .ToListAsync(cancellationToken);

        List<string> scopedPermissions = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.OrganizationId == organizationId && m.Status == MembershipStatus.Active)
            .SelectMany(m => m.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .ToListAsync(cancellationToken);

        return globalPermissions.Concat(scopedPermissions).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> GetGlobalPermissionsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct()
            .ToListAsync(cancellationToken);
}
