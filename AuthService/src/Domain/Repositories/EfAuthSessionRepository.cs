using Dprental.AuthService.Domain.Data;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Models;
using Dprental.AuthService.Domain.Utils;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Domain.Repositories;

/// <summary>EF Core persistence cho session và refresh-token rotation.</summary>
public sealed class EfAuthSessionRepository(AuthDbContext dbContext) : IAuthSessionRepository
{
    /// <inheritdoc />
    public Task<Session?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(session => session.Id == id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Session>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Sessions
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Session> CreateAsync(Session entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        dbContext.Sessions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<AuthSessionPrincipal?> CreateAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        AuthSessionPrincipal? principal = await GetActiveUserPrincipalAsync(userId, cancellationToken);
        if (principal is null)
        {
            return null;
        }

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

        return principal;
    }

    /// <inheritdoc />
    public async Task<bool> CreateGoogleLoginTicketAsync(
        Guid userId,
        string ticketHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        bool activeUserExists = await dbContext.Users
            .AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken);
        if (!activeUserExists)
        {
            return false;
        }

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

    /// <inheritdoc />
    public async Task<AuthSessionPrincipal?> ConsumeGoogleLoginTicketAndCreateSessionAsync(
        string ticketHash,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime sessionExpiresAtUtc,
        CancellationToken cancellationToken)
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

        AuthSessionPrincipal? principal = await GetActiveUserPrincipalAsync(ticket.UserId, cancellationToken);
        if (principal is null)
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

        return principal;
    }

    /// <inheritdoc />
    public async Task<AuthSessionPrincipal?> RotateAsync(
        string currentRefreshTokenHash,
        string replacementRefreshTokenHash,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        RefreshToken? currentToken = await dbContext.RefreshTokens
            .AsNoTracking()
            .Include(token => token.Session)
                .ThenInclude(session => session.User)
                    .ThenInclude(user => user.UserRoles)
                        .ThenInclude(userRole => userRole.Role)
                            .ThenInclude(role => role.RolePermissions)
                                .ThenInclude(rolePermission => rolePermission.Permission)
            .Include(token => token.Session.User.OrganizationMemberships)
                .ThenInclude(membership => membership.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(token => token.TokenHash == currentRefreshTokenHash, cancellationToken);

        if (currentToken is null)
        {
            return null;
        }

        if (currentToken.IsRevoked || currentToken.UsedAtUtc is not null || currentToken.RevokedAtUtc is not null)
        {
            await RevokeCompromisedSessionAsync(currentToken.SessionId, utcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (!IsUsable(currentToken.Session, currentToken, utcNow))
        {
            return null;
        }

        int usedTokenCount = await dbContext.RefreshTokens
            .Where(token => token.Id == currentToken.Id && token.UsedAtUtc == null && token.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.UsedAtUtc, utcNow), cancellationToken);

        if (usedTokenCount != 1)
        {
            // A second use of the rotated token is treated as reuse and invalidates the session.
            await RevokeCompromisedSessionAsync(currentToken.SessionId, utcNow, cancellationToken);
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

    /// <inheritdoc />
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
                .ThenInclude(membership => membership.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(cancellationToken);

        return user is null ? null : BuildPrincipal(user);
    }

    /// <summary>Gộp role, permission (global + theo organization active) và organization đang active thành principal.</summary>
    private static AuthSessionPrincipal BuildPrincipal(User user) =>
        new(
            user.Id,
            UserAccessResolver.Roles(user),
            UserAccessResolver.Permissions(user),
            UserAccessResolver.ActiveMemberships(user)
                .Select(membership => membership.OrganizationId)
                .ToArray());

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

    private Task<int> RevokeSessionAsync(Guid sessionId, DateTime utcNow, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .Where(session => session.Id == sessionId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc, utcNow), cancellationToken);
}
