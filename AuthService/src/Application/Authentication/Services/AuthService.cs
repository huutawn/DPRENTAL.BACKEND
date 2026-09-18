using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AuthService.Application.Interfaces;
using AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Authentication.Tokens;
using DpRent.Domain.Entities;

namespace AuthService.Application.Services;

public class AuthService : IAuthService
{
    private readonly IApplicationDbContext _context;
    private readonly IAccessTokenIssuer _tokenIssuer;

    public AuthService(IApplicationDbContext context, IAccessTokenIssuer tokenIssuer)
    {
        _context = context;
        _tokenIssuer = tokenIssuer;
    }

    public async Task<LoginResponseDto> LoginAsync(LoginRequestDto request)
    {
        // 1. Tìm account & Roles
        var user = await _context.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == request.Email);

        if (user == null || !user.IsActive)
            throw new UnauthorizedAccessException("Tài khoản không hợp lệ hoặc đã bị khóa.");

        // 2. Verify password
        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Tài khoản hoặc mật khẩu không chính xác.");

        // 3. Sử dụng cấu trúc Record mới để xin cấp Access Token
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();

        var tokenRequest = new AccessTokenRequest(user.Id, roles);

        var issuedToken = _tokenIssuer.Issue(tokenRequest);

        // 4. Create a cryptographically secure refresh token and store only its server-side hash.
        byte[] tokenBytes = new byte[64]; // 512 bits of randomness
        RandomNumberGenerator.Fill(tokenBytes);
        // Use URL-safe base64 so token can be transported easily (no padding issues in URLs).
        string refreshTokenString = Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        // Compute SHA-256 hash of the token for storage. Store as base64 string.
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(refreshTokenString));
        string tokenHash = Convert.ToBase64String(hashBytes);

        var session = new RefreshToken
        {
            TokenHash = tokenHash,
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.AddDays(7),
            IsRevoked = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.RefreshTokens.Add(session);
        await _context.SaveChangesAsync();

        // 5. Trả về kết quả
        return new LoginResponseDto
        {
            AccessToken = issuedToken.Value,
            RefreshToken = refreshTokenString,
            ExpiresIn = (int)(issuedToken.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds
        };
    }

    public async Task<LoginResponseDto> RefreshAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Refresh token is required.", nameof(refreshToken));

        // Compute hash of incoming refresh token
        byte[] incomingHash = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
        string incomingHashString = Convert.ToBase64String(incomingHash);

        // Find matching refresh token
        var existing = await _context.RefreshTokens
            .Include(rt => rt.User)
            .ThenInclude(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(rt => rt.TokenHash == incomingHashString);

        if (existing == null)
            throw new UnauthorizedAccessException("Invalid refresh token.");

        // If token is already revoked => reuse detected (possible token theft).
        if (existing.IsRevoked)
        {
            // Revoke all active refresh tokens for this user to mitigate compromise.
            var userId = existing.UserId;
            var tokensToRevoke = _context.RefreshTokens.Where(rt => rt.UserId == userId && !rt.IsRevoked).ToList();
            foreach (var t in tokensToRevoke)
            {
                t.IsRevoked = true;
                t.RevokedAt = DateTime.UtcNow;
                t.RevokedReason = "ReuseDetected";
            }

            await _context.SaveChangesAsync();

            throw new UnauthorizedAccessException("Refresh token reuse detected. All sessions have been revoked.");
        }

        if (existing.ExpiryDate < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Expired refresh token.");

        var user = existing.User;
        if (user == null || !user.IsActive)
            throw new UnauthorizedAccessException("User not found or is inactive.");

        // Issue new access token
        var roles = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        var tokenRequest = new AccessTokenRequest(user.Id, roles);
        var issuedToken = _tokenIssuer.Issue(tokenRequest);

        // Rotate refresh token: revoke existing and issue a new one
        existing.IsRevoked = true;
        existing.RevokedAt = DateTime.UtcNow;
        existing.RevokedReason = "Rotated";
        byte[] tokenBytes = new byte[64];
        RandomNumberGenerator.Fill(tokenBytes);
        string newRefreshToken = Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        byte[] newHash = SHA256.HashData(Encoding.UTF8.GetBytes(newRefreshToken));
        string newHashString = Convert.ToBase64String(newHash);

        var newSession = new RefreshToken
        {
            TokenHash = newHashString,
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.AddDays(7),
            IsRevoked = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.RefreshTokens.Add(newSession);
        // Link previous token to the new one for audit/lookup
        existing.ReplacedByTokenHash = newHashString;

        await _context.SaveChangesAsync();

        return new LoginResponseDto
        {
            AccessToken = issuedToken.Value,
            RefreshToken = newRefreshToken,
            ExpiresIn = (int)(issuedToken.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds
        };
    }
}