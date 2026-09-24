using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

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
        catch (DbUpdateException exception)
        {
            // Bỏ entity lỗi khỏi DbContext để lần SaveChanges sau không thử insert lại.
            dbContext.Entry(entity).State = EntityState.Detached;
            if (UniqueConstraintViolations.ToUserException(exception) is { } duplicate)
            {
                throw duplicate;
            }

            throw;
        }
    }

    public async Task<User> CreateWithRoleAsync(User user, Guid roleId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        UserRole userRole = new() { UserId = user.Id, RoleId = roleId };
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
                    dbContext.UserRoles.Add(userRole);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return user;
                }
                catch (DbUpdateException exception)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    Detach(user, userRole);
                    if (UniqueConstraintViolations.ToUserException(exception) is { } duplicate)
                    {
                        throw duplicate;
                    }

                    throw;
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    Detach(user, userRole);
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
            dbContext.UserRoles.Add(userRole);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return user;
            }
            catch
            {
                // Provider không quan hệ (InMemory) không kiểm tra unique index nên không có lỗi trùng để dịch.
                Detach(user, userRole);
                throw;
            }
        }
    }

    // Bỏ cả User lẫn UserRole khỏi DbContext; nếu chỉ bỏ User, UserRole mồ côi sẽ bị insert ở lần SaveChanges sau.
    private void Detach(User user, UserRole userRole)
    {
        dbContext.Entry(user).State = EntityState.Detached;
        dbContext.Entry(userRole).State = EntityState.Detached;
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            if (UniqueConstraintViolations.ToUserException(exception) is { } duplicate)
            {
                throw duplicate;
            }

            throw;
        }
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

    public Task<bool> CitizenIdExistsAsync(string citizenId, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.CitizenId == citizenId, cancellationToken);

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
