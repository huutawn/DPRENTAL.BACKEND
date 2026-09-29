using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Dprental.AuthService.Domain.Repositories;

namespace Dprental.AuthService.Domain.Repositories;

/// <summary>EF Core persistence cho session và refresh-token rotation.</summary>
public sealed class EfAuthSessionRepository(AuthDbContext dbContext, TimeProvider timeProvider) : IAuthSessionRepository
{
    public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(session => session.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Sessions
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        dbContext.Sessions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task RevokeAllSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = timeProvider.GetUtcNow().UtcDateTime;
            
        List<Session> sessions = await dbContext.Sessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        foreach (Session session in sessions)
        {
            session.RevokedAtUtc = utcNow;
        }

        List<RefreshToken> refreshTokens = await dbContext.RefreshTokens
            .Where(token => token.UserId == userId && !token.IsRevoked)
            .ToListAsync(cancellationToken);

        foreach (RefreshToken token in refreshTokens)
        {
            token.IsRevoked = true;
            token.RevokedAtUtc = utcNow;
            token.RevokedReason = "RevokeAllSessions";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(Session entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        bool exists = await dbContext.Sessions
            .AsNoTracking()
            .AnyAsync(session => session.Id == entity.Id, cancellationToken);
        if (!exists) return false;

        dbContext.Sessions.Update(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Session? session = await dbContext.Sessions.FindAsync([id], cancellationToken);
        if (session is null) return false;

        dbContext.Sessions.Remove(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<AuthSessionPrincipal?> CreateAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        string? deviceName,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        AuthSessionPrincipal? principal = await GetActiveUserPrincipalAsync(userId, cancellationToken);
        if (principal is null) return null;

        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DeviceName = deviceName,
            IpAddress = ipAddress,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = expiresAtUtc
        };
        RefreshToken refreshToken = new()
        {
            Id = Guid.NewGuid(),
            TokenHash = refreshTokenHash,
            CreatedAt = utcNow,
            UserId = userId,
            SessionId = session.Id,
            ExpiryDate = expiresAtUtc,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = expiresAtUtc,
            IsRevoked = false
        };

        dbContext.Sessions.Add(session);
        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return principal;
    }

    public Task<RefreshToken?> GetRefreshTokenWithSessionAndUserAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        return dbContext.RefreshTokens
            .AsNoTracking()
            .Include(t => t.Session)
                .ThenInclude(s => s.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
    }

    public async Task<AuthSessionPrincipal?> RotateAsync(
        string currentRefreshTokenHash,
        string replacementRefreshTokenHash,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        
        RefreshToken? currentToken = await dbContext.RefreshTokens
            .AsNoTracking()
            .Include(token => token.Session)
                .ThenInclude(session => session.User)
                    .ThenInclude(user => user.UserRoles)
                        .ThenInclude(userRole => userRole.Role)
                            .ThenInclude(role => role.RolePermissions)
                                .ThenInclude(rolePermission => rolePermission.Permission)
            .Include(token => token.Session.User.OrganizationMemberships)
                .ThenInclude(membership => membership.Organization)
            .Include(token => token.Session.User.OrganizationMemberships)
                .ThenInclude(membership => membership.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(token => token.TokenHash == currentRefreshTokenHash, cancellationToken);

        if (currentToken is null) return null;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (currentToken.IsRevoked || currentToken.UsedAtUtc is not null || currentToken.RevokedAtUtc is not null)
        {
            await RevokeAllSessionsForUserAsync(currentToken.Session.UserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (!IsUsable(currentToken.Session, currentToken, utcNow)) return null;

        int usedTokenCount = await dbContext.RefreshTokens
            .Where(token => token.Id == currentToken.Id && token.UsedAtUtc == null && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAtUtc, utcNow), cancellationToken);

        if (usedTokenCount != 1)
        {
            await RevokeAllSessionsForUserAsync(currentToken.Session.UserId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await dbContext.RefreshTokens
            .Where(token => token.Id == currentToken.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.UsedAtUtc, utcNow)
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, utcNow)
                .SetProperty(token => token.RevokedAtUtc, utcNow)
                .SetProperty(token => token.RevokedReason, "Rotated")
                .SetProperty(token => token.ReplacedByTokenHash, replacementRefreshTokenHash), cancellationToken);

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = replacementRefreshTokenHash,
            CreatedAt = utcNow,
            UserId = currentToken.Session.UserId,
            SessionId = currentToken.SessionId,
            ExpiryDate = currentToken.Session.ExpiresAtUtc,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = currentToken.Session.ExpiresAtUtc,
            IsRevoked = false
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return BuildPrincipal(currentToken.Session.User);
    }

    public async Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken)
    {
        Guid? sessionId = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(token => token.TokenHash == refreshTokenHash)
            .Select(token => (Guid?)token.SessionId)
            .SingleOrDefaultAsync(cancellationToken);

        if (sessionId is not null)
        {
            await RevokeSessionAsync(sessionId.Value, utcNow, cancellationToken);
        }
    }

    private async Task<AuthSessionPrincipal?> GetActiveUserPrincipalAsync(Guid userId, CancellationToken cancellationToken)
    {
        User? user = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.IsActive)
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
            .SingleOrDefaultAsync(cancellationToken);

        return user is null ? null : BuildPrincipal(user);
    }

    private static AuthSessionPrincipal BuildPrincipal(User user)
    {
        string[] roles = user.UserRoles?
            .Select(ur => ur.Role.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToArray() ?? [];
        var globalPermissions = user.UserRoles?
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code) ?? [];

        var orgPermissions = user.OrganizationMemberships?
            .SelectMany(m => m.Role.RolePermissions)
            .Select(rp => rp.Permission.Code) ?? [];

        string[] permissions = globalPermissions.Concat(orgPermissions)
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .ToArray();
        Guid[] orgIds = user.OrganizationMemberships?
            .Select(m => m.OrganizationId)
            .Distinct()
            .ToArray() ?? [];

        return new AuthSessionPrincipal(user.Id, roles, permissions, orgIds);
    }

    private static bool IsUsable(Session session, RefreshToken refreshToken, DateTime utcNow) =>
        session.RevokedAtUtc is null &&
        session.ExpiresAtUtc > utcNow &&
        refreshToken.ExpiresAtUtc > utcNow &&
        session.User.IsActive;

    private async Task RevokeCompromisedSessionAsync(Guid sessionId, DateTime utcNow, CancellationToken cancellationToken)
    {
        await dbContext.RefreshTokens
            .Where(token => token.SessionId == sessionId && !token.IsRevoked)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, utcNow)
                .SetProperty(token => token.RevokedAtUtc, utcNow)
                .SetProperty(token => token.RevokedReason, "ReuseDetected"), cancellationToken);

        await RevokeSessionAsync(sessionId, utcNow, cancellationToken);
    }

    public async Task<Guid?> GetUserIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var tokenRecord = await dbContext.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        return tokenRecord?.UserId;
    }

    public async Task<IReadOnlyList<Session>> GetSessionsForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.Sessions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

    public Task<Guid?> GetSessionIdByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        dbContext.RefreshTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => (Guid?)t.SessionId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> RevokeSessionForUserAsync(Guid userId, Guid sessionId, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        Session? session = await dbContext.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, cancellationToken);
        if (session is null) return false;

        session.RevokedAtUtc ??= utcNow;

        List<RefreshToken> childTokens = await dbContext.RefreshTokens
            .Where(t => t.SessionId == sessionId && !t.IsRevoked)
            .ToListAsync(cancellationToken);

        foreach (RefreshToken token in childTokens)
        {
            token.IsRevoked = true;
            token.RevokedAtUtc = utcNow;
            token.RevokedReason = "UserRevoked";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private Task<int> RevokeSessionAsync(Guid sessionId, DateTime utcNow, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .Where(session => session.Id == sessionId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc, utcNow), cancellationToken);
}
