using Dprental.AuthService.Common.Enums;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using Dprental.AuthService.Domain.IRepositories;

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

    public async Task<PagedResult<OrganizationMembership>> GetMembersAsync(Guid organizationId, PageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IQueryable<OrganizationMembership> query = dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == organizationId);

        string? searchTerm = request.GetSearchTerm();
        if (searchTerm is not null)
        {
            // EF Core translates these overloads to SQL; culture/StringComparison overloads are not translatable.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(m => m.User.FullName.ToLower().Contains(searchTerm) ||
                m.User.Email.ToLower().Contains(searchTerm) ||
                m.Role.Name.ToLower().Contains(searchTerm));
#pragma warning restore CA1304, CA1311, CA1862
        }

        int totalCount = await query
            .CountAsync(cancellationToken);

        int skip = (request.Page - 1) * request.PageSize;
        List<OrganizationMembership> items = await query
            .Include(m => m.User)
            .Include(m => m.Role)
                .ThenInclude(r => r.RolePermissions)
                    .ThenInclude(rp => rp.Permission)
            .OrderBy(m => m.JoinedAtUtc)
            .ThenBy(m => m.Id)
            .Skip(skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<OrganizationMembership>(items, request.Page, request.PageSize, totalCount);
    }

    public Task<bool> MembershipExistsAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(m => m.OrganizationId == organizationId && m.UserId == userId, cancellationToken);

    public async Task<OrganizationMembership> AddMemberAsync(OrganizationMembership membership, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(membership);

        dbContext.OrganizationMemberships.Add(membership);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return membership;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(membership).State = EntityState.Detached;
            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
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

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(existing).State = EntityState.Detached;
            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
        }
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
