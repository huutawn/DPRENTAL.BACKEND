using AuthService.Application.Authentication.Sessions;
using DpRent.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Infrastructure.Authentication;

/// <summary>EF Core persistence cho session và refresh-token rotation.</summary>
public sealed class SessionStore(ApplicationDbContext dbContext) : IAuthenticationSessionStore
{
    /// <inheritdoc />
    public async Task<SessionPrincipal?> CreateAsync(
        Guid userId,
        string refreshTokenHash,
        DateTime utcNow,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken)
    {
        SessionPrincipal? principal = await GetActiveUserPrincipalAsync(userId, cancellationToken);
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
            SessionId = session.Id,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = expiresAtUtc
        };

        dbContext.Sessions.Add(session);
        dbContext.RefreshTokens.Add(refreshToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return principal;
    }

    /// <inheritdoc />
    public async Task<SessionPrincipal?> RotateAsync(
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
            .SingleOrDefaultAsync(token => token.TokenHash == currentRefreshTokenHash, cancellationToken);

        if (currentToken is null)
        {
            return null;
        }

        if (currentToken.UsedAtUtc is not null || currentToken.RevokedAtUtc is not null)
        {
            await RevokeSessionAsync(currentToken.SessionId, utcNow, cancellationToken);
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
            // Một request song song đã dùng token trước; không thu hồi session hợp lệ của request thắng cuộc.
            return null;
        }

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = replacementRefreshTokenHash,
            SessionId = currentToken.SessionId,
            CreatedAtUtc = utcNow,
            ExpiresAtUtc = currentToken.Session.ExpiresAtUtc
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SessionPrincipal(
            currentToken.Session.UserId,
            currentToken.Session.User.UserRoles.Select(userRole => userRole.Role.Name).ToArray());
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

    /// <summary>Đọc user active và roles để phát hành access token.</summary>
    private async Task<SessionPrincipal?> GetActiveUserPrincipalAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId && user.IsActive)
            .Select(user => new SessionPrincipal(
                user.Id,
                user.UserRoles.Select(userRole => userRole.Role.Name).ToArray()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Kiểm tra session, token và user còn có thể dùng để refresh hay không.</summary>
    private static bool IsUsable(Session session, RefreshToken refreshToken, DateTime utcNow) =>
        session.RevokedAtUtc is null &&
        session.ExpiresAtUtc > utcNow &&
        refreshToken.ExpiresAtUtc > utcNow &&
        session.User.IsActive;

    /// <summary>Thu hồi session một lần duy nhất; gọi lặp lại vẫn an toàn.</summary>
    private Task<int> RevokeSessionAsync(Guid sessionId, DateTime utcNow, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .Where(session => session.Id == sessionId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc, utcNow), cancellationToken);
}
