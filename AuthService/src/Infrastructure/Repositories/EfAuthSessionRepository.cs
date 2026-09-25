using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Infrastructure.Repositories;

/// <summary>
/// EF Core persistence cho session và refresh-token rotation.
/// Bảo đảm tính nguyên tử của thao tác DB thông qua conditional updates và transactions.
/// </summary>
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
        if (!exists)
        {
            return false;
        }

        dbContext.Sessions.Update(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Session? session = await dbContext.Sessions.FindAsync([id], cancellationToken);
        if (session is null)
        {
            return false;
        }

        dbContext.Sessions.Remove(session);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task CreateSessionAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
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
    }

    public async Task<bool> CreateGoogleLoginTicketAsync(
        Guid userId,
        string ticketHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        dbContext.GoogleLoginTickets.Add(new GoogleLoginTicket
        {
            Id = Guid.NewGuid(),
            TicketHash = ticketHash,
            UserId = userId,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = expiresAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<Guid?> ConsumeGoogleLoginTicketAndCreateSessionAsync(
        string ticketHash,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime sessionExpiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        GoogleLoginTicket? ticket = await dbContext.GoogleLoginTickets
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TicketHash == ticketHash, cancellationToken);

        if (ticket is null || ticket.ExpiresAtUtc <= utcNow || ticket.UsedAtUtc is not null)
        {
            return null;
        }

        User? ticketUser = dbContext.Database.IsRelational()
            ? await dbContext.Users
                .FromSqlInterpolated($"SELECT * FROM auth.\"Users\" WHERE \"Id\" = {ticket.UserId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == ticket.UserId, cancellationToken);
        if (ticketUser is null || !ticketUser.IsActive)
        {
            return null;
        }

        int consumed;
        if (dbContext.Database.IsRelational())
        {
            consumed = await dbContext.GoogleLoginTickets
                .Where(candidate => candidate.Id == ticket.Id
                    && candidate.UsedAtUtc == null
                    && candidate.ExpiresAtUtc > utcNow)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.UsedAtUtc, utcNow), cancellationToken);
        }
        else
        {
            GoogleLoginTicket? trackedTicket = await dbContext.GoogleLoginTickets
                .SingleOrDefaultAsync(candidate => candidate.Id == ticket.Id
                    && candidate.UsedAtUtc == null
                    && candidate.ExpiresAtUtc > utcNow, cancellationToken);
            if (trackedTicket is null)
            {
                return null;
            }

            trackedTicket.UsedAtUtc = utcNow;
            consumed = 1;
        }

        if (consumed != 1)
        {
            return null;
        }

        Session session = new()
        {
            Id = Guid.NewGuid(),
            UserId = ticket.UserId,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = sessionExpiresAtUtc
        };

        dbContext.Sessions.Add(session);
        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = refreshTokenHash,
            CreatedAt = utcNow,
            UserId = ticket.UserId,
            SessionId = session.Id,
            ExpiryDate = sessionExpiresAtUtc,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = sessionExpiresAtUtc,
            IsRevoked = false
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return ticket.UserId;
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

    public async Task<bool> RotateTokenAsync(
        Guid currentTokenId,
        Guid sessionId,
        Guid userId,
        string replacementHash,
        DateTime sessionExpiresAtUtc,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        int usedTokenCount;
        if (dbContext.Database.IsRelational())
        {
            usedTokenCount = await dbContext.RefreshTokens
                .Where(t => t.Id == currentTokenId && t.UsedAtUtc == null && !t.IsRevoked)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.UsedAtUtc, utcNow)
                    .SetProperty(t => t.IsRevoked, true)
                    .SetProperty(t => t.RevokedAt, utcNow)
                    .SetProperty(t => t.RevokedAtUtc, utcNow)
                    .SetProperty(t => t.RevokedReason, "Rotated")
                    .SetProperty(t => t.ReplacedByTokenHash, replacementHash), cancellationToken);
        }
        else
        {
            RefreshToken? tracked = await dbContext.RefreshTokens
                .FirstOrDefaultAsync(t => t.Id == currentTokenId && t.UsedAtUtc == null && !t.IsRevoked, cancellationToken);
            if (tracked is null)
            {
                return false;
            }

            tracked.UsedAtUtc = utcNow;
            tracked.IsRevoked = true;
            tracked.RevokedAt = utcNow;
            tracked.RevokedAtUtc = utcNow;
            tracked.RevokedReason = "Rotated";
            tracked.ReplacedByTokenHash = replacementHash;
            usedTokenCount = 1;
        }

        if (usedTokenCount != 1)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return false;
        }

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = replacementHash,
            CreatedAt = utcNow,
            UserId = userId,
            SessionId = sessionId,
            ExpiryDate = sessionExpiresAtUtc,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = sessionExpiresAtUtc,
            IsRevoked = false
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    public async Task RevokeByRefreshTokenHashAsync(string refreshTokenHash, DateTime utcNow, CancellationToken cancellationToken = default)
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

<<<<<<< HEAD:AuthService/src/Domain/Repositories/EfAuthSessionRepository.cs
    /// <summary>Đọc user active cùng roles, permissions (global + theo organization) và organization đang active để phát hành access token.</summary>
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

    /// <summary>Gộp role, permission (global + theo organization active), organization đang active và trạng thái hoàn thiện hồ sơ thành principal.</summary>
    private static AuthSessionPrincipal BuildPrincipal(User user) =>
        new(
            user.Id,
            UserAccessResolver.Roles(user),
            UserAccessResolver.Permissions(user),
            UserAccessResolver.ActiveMemberships(user)
                .Select(membership => membership.OrganizationId)
                .ToArray(),
            user.IsProfileCompleted);

    /// <summary>Kiểm tra session, token và user còn có thể dùng để refresh hay không.</summary>
    private static bool IsUsable(Session session, RefreshToken refreshToken, DateTime utcNow) =>
        session.RevokedAtUtc is null &&
        session.ExpiresAtUtc > utcNow &&
        refreshToken.ExpiresAtUtc > utcNow &&
        session.User.IsActive;

    /// <summary>Thu hồi session một lần duy nhất; gọi lặp lại vẫn an toàn.</summary>
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

=======
>>>>>>> b8a22d3c2f59a49031461f3f63c44a44371afe2d:AuthService/src/Infrastructure/Repositories/EfAuthSessionRepository.cs
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
        if (session is null)
        {
            return false;
        }

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
