using Dprental.AuthService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Domain.Repositories;


public sealed class EfUserRepository(AuthDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == id, cancellationToken);

    public Task<User?> GetByIdWithRolesAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .Include(u => u.UserRoles)
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
            dbContext.Entry(entity).State = EntityState.Detached;
            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
            }
            throw;
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
                    if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
                    {
                        throw constraintEx;
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
                if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
                {
                    throw constraintEx;
                }
                throw;
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            if (DbExceptionTranslator.TryTranslate(exception, out var constraintEx))
            {
                throw constraintEx;
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

    public async Task<(IReadOnlyCollection<User> Items, int TotalCount)> GetUsersPagedAsync(
    int skip, int take, string? search = null, string? type = null,
    CancellationToken cancellationToken = default)
    {
        var query = dbContext.Users.AsNoTracking().AsQueryable();

        // 1. Lọc theo keyword
        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();

            if (dbContext.Database.IsNpgsql())
            {
                var pattern = $"%{EscapeLike(keyword)}%";
                query = query.Where(u =>
                    EF.Functions.ILike(u.Email, pattern) ||
                    EF.Functions.ILike(u.FullName, pattern));
            }
            else
            {
                // Chỉ chạy trên InMemory (test), thực thi trong bộ nhớ nên dùng được StringComparison
                query = query.Where(u =>
                    u.Email.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    u.FullName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }
        }

        // 2. Lọc theo role
        if (!string.IsNullOrWhiteSpace(type))
        {
            var roleName = EscapeLike(type.Trim());
            query = query.Where(u => u.UserRoles.Any(ur =>
                EF.Functions.ILike(ur.Role.Name, roleName)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var users = await query
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .OrderByDescending(u => u.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (users, totalCount);
    }

    // Escape ký tự đặc biệt của LIKE để user gõ "%" hay "_" không bị hiểu là wildcard
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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

    public async Task<bool> ChangePasswordAndRevokeSessionsAsync(
        Guid userId,
        string newPasswordHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        User? user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = newPasswordHash;
        user.HasPassword = true;

        List<Session> sessions = await dbContext.Sessions
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (Session s in sessions)
        {
            s.RevokedAtUtc = utcNow;
        }

        List<RefreshToken> tokens = await dbContext.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ToListAsync(cancellationToken);
        foreach (RefreshToken t in tokens)
        {
            t.IsRevoked = true;
            t.RevokedAtUtc = utcNow;
            t.RevokedReason = "PasswordChanged";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> ResetPasswordWithTokenAsync(
        Guid userId,
        string tokenHash,
        string newPasswordHash,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        int tokenConsumed;
        if (dbContext.Database.IsRelational())
        {
            tokenConsumed = await dbContext.RecoveryTokens
                .Where(t => t.TokenHash == tokenHash
                    && t.UserId == userId
                    && t.Purpose == "ResetPassword"
                    && t.UsedAtUtc == null
                    && t.ExpiresAtUtc > utcNow)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, utcNow), cancellationToken);
        }
        else
        {
            RecoveryToken? trackedToken = await dbContext.RecoveryTokens
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash
                    && t.UserId == userId
                    && t.Purpose == "ResetPassword"
                    && t.UsedAtUtc == null
                    && t.ExpiresAtUtc > utcNow, cancellationToken);
            if (trackedToken is null)
            {
                return false;
            }
            trackedToken.UsedAtUtc = utcNow;
            tokenConsumed = 1;
        }

        if (tokenConsumed != 1)
        {
            return false;
        }

        User? user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = newPasswordHash;
        user.HasPassword = true;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        List<Session> sessions = await dbContext.Sessions
            .Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (Session s in sessions)
        {
            s.RevokedAtUtc = utcNow;
        }

        List<RefreshToken> tokens = await dbContext.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ToListAsync(cancellationToken);
        foreach (RefreshToken t in tokens)
        {
            t.IsRevoked = true;
            t.RevokedAtUtc = utcNow;
            t.RevokedReason = "PasswordReset";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return true;
    }

    public Task<bool> IsUserActiveAsync(Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken);
}
