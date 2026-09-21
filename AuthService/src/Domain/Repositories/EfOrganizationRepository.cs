using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfOrganizationRepository(AuthDbContext dbContext) : IOrganizationRepository
{
    public Task<Organization?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(org => org.Id == id, cancellationToken);

    public Task<Organization?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(org => org.Slug == slug, cancellationToken);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken = default) =>
        dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(org => org.Slug == slug, cancellationToken);

    public async Task<IReadOnlyList<Organization>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Organizations
            .AsNoTracking()
            .OrderBy(org => org.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Organization>> GetUserOrganizationsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && m.Organization.IsActive)
            .Select(m => m.Organization)
            .Distinct()
            .OrderBy(org => org.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<Organization> CreateAsync(
        Organization organization,
        OrganizationMembership initialMembership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(initialMembership);

        if (await SlugExistsAsync(organization.Slug, cancellationToken))
        {
            throw new DuplicateSlugException("Slug của organization đã tồn tại.");
        }

        dbContext.Organizations.Add(organization);
        dbContext.OrganizationMemberships.Add(initialMembership);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return organization;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(organization).State = EntityState.Detached;
            dbContext.Entry(initialMembership).State = EntityState.Detached;
            throw new DuplicateSlugException("Slug của organization đã tồn tại.", exception);
        }
    }

    public async Task<bool> UpdateAsync(Organization organization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);

        bool exists = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(org => org.Id == organization.Id, cancellationToken);

        if (!exists)
        {
            return false;
        }

        dbContext.Organizations.Update(organization);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
