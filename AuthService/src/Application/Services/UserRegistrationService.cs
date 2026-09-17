using System.Diagnostics.CodeAnalysis;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Errors;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.AspNetCore.Identity;

namespace Dprental.AuthService.Application.Services;

public sealed class UserRegistrationService(
    IUserRepository userRepository,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider) : IUserRegistrationService
{
    public async Task<Result<AccountCreatedResponse>> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email = NormalizeEmail(request.Email);
        if (await userRepository.EmailExistsAsync(email, cancellationToken))
        {
            return UserErrors.EmailAlreadyExists;
        }

        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = request.FullName.Trim(),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            IsActive = true,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);

        if (!await userRepository.TryCreateAsync(user, cancellationToken))
        {
            return UserErrors.EmailAlreadyExists;
        }

        return new AccountCreatedResponse(user.Id);
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Email được lưu và so sánh ở dạng chữ thường theo quy ước phổ biến.")]
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
