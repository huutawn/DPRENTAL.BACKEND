using System;
using System.Linq;
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

        // 4. Tạo Refresh Token
        var refreshTokenString = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var session = new RefreshToken
        {
            Token = refreshTokenString,
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
}