using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;
using Microsoft.AspNetCore.Identity;

namespace Dprental.AuthService.Application.Services;

/// <summary>Quy tắc dựng tài khoản customer, dùng chung cho đăng ký cá nhân và tài khoản Owner khi đăng ký tổ chức.</summary>
internal static class CustomerAccountRules
{
    internal const int MinimumAge = 18;

    internal static bool IsUnderage(DateOnly dateOfBirth, DateTimeOffset utcNow) =>
        dateOfBirth.AddYears(MinimumAge) > DateOnly.FromDateTime(utcNow.UtcDateTime);

    /// <summary>
    /// Kiểm tra tuổi và trùng email trước khi ghi. Race condition vẫn được chặn bởi unique index.
    /// Không nhận/kiểm tra CCCD ở đây — bước đăng ký không thu thập CCCD (xem <see cref="RegisterRequest"/>).
    /// </summary>
    internal static async Task<Error?> ValidateAsync(
        RegisterRequest request,
        string normalizedEmail,
        IUserRepository userRepository,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        if (request.DateOfBirth is DateOnly dateOfBirth
            && IsUnderage(dateOfBirth, utcNow))
        {
            return AuthErrors.User.Underage;
        }

        if (await userRepository.EmailExistsAsync(normalizedEmail, cancellationToken))
        {
            return AuthErrors.User.EmailAlreadyExists;
        }

        return null;
    }

    internal static User Create(
        RegisterRequest request,
        string normalizedEmail,
        IPasswordHasher<User> passwordHasher,
        DateTime utcNow)
    {
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            FullName = request.FullName.Trim(),
            // CitizenId để trống lúc đăng ký — chỉ được set khi đăng ký tổ chức hoặc hoàn thiện hồ sơ để thuê cá nhân.
            AvatarUrl = NullIfBlank(request.AvatarUrl),
            PhoneNumber = NullIfBlank(request.PhoneNumber),
            DateOfBirth = request.DateOfBirth,
            CreatedAt = utcNow,
            HasPassword = true,
            IsActive = true,
        };
        user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
        return user;
    }

    /// <summary>Che CCCD, chỉ để lộ 4 số cuối.</summary>
    internal static string? MaskCitizenId(string? citizenId) =>
        citizenId switch
        {
            null or "" => null,
            { Length: <= 4 } => new string('*', citizenId.Length),
            _ => new string('*', citizenId.Length - 4) + citizenId[^4..]
        };

    internal static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
