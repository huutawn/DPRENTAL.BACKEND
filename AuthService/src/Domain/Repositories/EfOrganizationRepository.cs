using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using Dprental.AuthService.Domain.IRepositories;

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

        int totalCount = await dbContext.Organizations.CountAsync(cancellationToken);

        int skip = (request.Page - 1) * request.PageSize;
        List<Organization> items = await dbContext.Organizations
            .AsNoTracking()
            .OrderBy(o => o.Name)
            .Skip(skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Organization>(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<PagedResult<Organization>> GetUserOrganizationsAsync(Guid userId, PageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.Organization);

        int totalCount = await query.CountAsync(cancellationToken);

        int skip = (request.Page - 1) * request.PageSize;
        List<Organization> items = await query
            .OrderBy(o => o.Name)
            .Skip(skip)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Organization>(items, request.Page, request.PageSize, totalCount);
    }

    public async Task<Organization> CreateAsync(
        Organization organization,
        OrganizationMembership initialMembership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(initialMembership);

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

            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
            }

            throw;
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(organization).State = EntityState.Detached;
            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
        }
    }

    public async Task LockOrganizationsAsync(IReadOnlyList<Guid> sortedOrganizationIds, CancellationToken cancellationToken = default)
    {
        if (sortedOrganizationIds.Count == 0)
        {
            return;
        }

        if (dbContext.Database.IsRelational())
        {
            Guid[] ids = sortedOrganizationIds.ToArray();
            await dbContext.Organizations
                .FromSqlRaw("SELECT * FROM auth.\"Organizations\" WHERE \"Id\" = ANY({0}) ORDER BY \"Id\" FOR UPDATE", ids)
                .ToListAsync(cancellationToken);
        }
        else
        {
            Guid[] ids = sortedOrganizationIds.ToArray();
            await dbContext.Organizations
                .Where(o => ids.Contains(o.Id))
                .ToListAsync(cancellationToken);
        }
    }

    public async Task<IExecutionTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.IsRelational())
        {
            var efTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            return new EfExecutionTransaction(efTransaction);
        }
        return new NullExecutionTransaction();
    }

    private sealed class EfExecutionTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) : IExecutionTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed class NullExecutionTransaction : IExecutionTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
