using Dprental.AuthService.Common;
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

    public async Task<PagedResult<Organization>> GetAllAsync(PageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IQueryable<Organization> query = dbContext.Organizations.AsNoTracking();
        int totalCount = await query.CountAsync(cancellationToken);

        List<Organization> items = await query
            .OrderBy(org => org.CreatedAtUtc)
            .ThenBy(org => org.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return PagedResult.Create(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<PagedResult<Organization>> GetUserOrganizationsAsync(Guid userId, PageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IQueryable<Organization> query = dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && m.Organization.Status == OrganizationStatus.Active)
            .Select(m => m.Organization)
            .Distinct();

        int totalCount = await query.CountAsync(cancellationToken);

        List<Organization> items = await query
            .OrderBy(org => org.CreatedAtUtc)
            .ThenBy(org => org.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return PagedResult.Create(items, request.Page, request.PageSize, totalCount);
    }

    /// <summary>
    /// Tạo organization và membership ban đầu trong một transaction. Ném <see cref="Exceptions.DuplicateSlugException"/>
    /// hoặc <see cref="Exceptions.DuplicateTaxCodeException"/> khi vi phạm unique.
    /// </summary>
    public async Task<Organization> CreateAsync(
        Organization organization,
        OrganizationMembership initialMembership,
        CancellationToken cancellationToken = default)
        => await CreateAsync(organization, initialMembership, null, cancellationToken);

    public async Task<Organization> CreateAsync(
        Organization organization,
        OrganizationMembership initialMembership,
        NavigationCatalog? navigationCatalog,
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
        if (navigationCatalog is not null)
        {
            dbContext.NavigationCatalogs.Add(navigationCatalog);
        }
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return organization;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(organization).State = EntityState.Detached;
            dbContext.Entry(initialMembership).State = EntityState.Detached;
            if (navigationCatalog is not null)
            {
                dbContext.Entry(navigationCatalog).State = EntityState.Detached;
            }

            if (UniqueConstraintViolations.IsUniqueViolation(exception))
            {
                throw UniqueConstraintViolations.ToOrganizationException(exception);
            }

            throw new DuplicateSlugException("Slug của organization đã tồn tại.", exception);
        }
    }

    public Task<bool> TaxCodeExistsAsync(string taxCode, CancellationToken cancellationToken = default) =>
        dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(org => org.TaxCode == taxCode, cancellationToken);

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
