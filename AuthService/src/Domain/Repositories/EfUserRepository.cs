using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dprental.AuthService.Domain.Repositories;

public sealed class EfUserRepository(AuthDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<User> CreateAsync(User entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        dbContext.Users.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return entity;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Bỏ entity lỗi khỏi DbContext để lần SaveChanges sau không thử insert lại.
            dbContext.Entry(entity).State = EntityState.Detached;
            throw new DuplicateEmailException("Email đã được sử dụng.", exception);
        }
    }

    public async Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (dbContext.Database.IsRelational())
        {
            var executionStrategy = dbContext.Database.CreateExecutionStrategy();
            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    bool roleExists = await dbContext.Roles.AnyAsync(r => r.Id == roleId, cancellationToken);
                    if (!roleExists)
                    {
                        throw new KeyNotFoundException($"Role {roleId} does not exist.");
                    }

                    dbContext.Users.Add(user);
                    dbContext.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return user;
                }
                catch (DbUpdateException exception)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    dbContext.Entry(user).State = EntityState.Detached;
                    if (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
                        exception.Message.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
                        exception.InnerException?.Message.Contains("Unique", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        throw new DuplicateEmailException("Email đã được sử dụng.", exception);
                    }
                    throw;
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    dbContext.Entry(user).State = EntityState.Detached;
                    throw;
                }
            });
        }
        else
        {
            bool roleExists = await dbContext.Roles.AnyAsync(r => r.Id == roleId, cancellationToken);
            if (!roleExists)
            {
                throw new KeyNotFoundException($"Role {roleId} does not exist.");
            }

            dbContext.Users.Add(user);
            dbContext.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roleId });
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return user;
            }
            catch (DbUpdateException exception)
            {
                dbContext.Entry(user).State = EntityState.Detached;
                throw new DuplicateEmailException("Email đã được sử dụng.", exception);
            }
        }
    }

    public async Task<bool> UpdateAsync(User entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        bool exists = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == entity.Id, cancellationToken);
        if (!exists)
        {
            return false;
        }

        dbContext.Users.Update(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        User? user = await dbContext.Users.FindAsync([id], cancellationToken);
        if (user is null)
        {
            return false;
        }

        dbContext.Users.Remove(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Users.AsNoTracking();

        int totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.Email == normalizedEmail, cancellationToken);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Email == normalizedEmail, cancellationToken);

    public Task<User?> GetByIdWithAccessAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .AsSplitQuery()
            .Include(user => user.UserRoles)
                .ThenInclude(userRole => userRole.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .Include(user => user.OrganizationMemberships)
                .ThenInclude(membership => membership.Organization)
            .Include(user => user.OrganizationMemberships)
                .ThenInclude(membership => membership.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(user => user.Id == id && user.IsActive, cancellationToken);
}
