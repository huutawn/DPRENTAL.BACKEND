using System.Diagnostics.CodeAnalysis;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Auth;
using Dprental.AuthService.Domain.DTOs.User;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.Errors;
using Dprental.AuthService.Domain.Exceptions;
using Dprental.AuthService.Domain.IRepositories;
using Dprental.AuthService.Domain.Utils;
using Microsoft.AspNetCore.Identity;

namespace Dprental.AuthService.Application.Services;

public sealed class UserService(
    IUserRepository userRepository,
    IPasswordHasher<User> passwordHasher,
    TimeProvider timeProvider) : IUserService
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

        try
        {
            await userRepository.CreateAsync(user, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            // Request khác đã đăng ký cùng email ngay sau bước kiểm tra ở trên.
            return UserErrors.EmailAlreadyExists;
        }

        return new AccountCreatedResponse(user.Id);
    }

    public async Task<Guid?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string email = NormalizeEmail(request.Email);
        User? user = await userRepository.FindByEmailAsync(email, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        PasswordVerificationResult verification = passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            request.Password);

        return verification is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded
            ? user.Id
            : null;
    }

    public async Task<Result<CurrentUserResponse>> GetCurrentUserProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        User? user = await userRepository.GetByIdWithAccessAsync(userId, cancellationToken);
        if (user is null)
        {
            return UserErrors.UserNotFound;
        }

        UserOrganizationResponse[] organizations = UserAccessResolver.ActiveMemberships(user)
            .Select(membership => new UserOrganizationResponse(
                membership.OrganizationId,
                membership.Organization.Name,
                membership.Organization.Slug,
                membership.Organization.Type,
                membership.Role.Name))
            .ToArray();

        return new CurrentUserResponse(
            user.Id,
            user.Email,
            user.FullName,
            UserAccessResolver.Roles(user),
            UserAccessResolver.Permissions(user),
            organizations,
            user.CreatedAt);
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Email được lưu và so sánh ở dạng chữ thường theo quy ước phổ biến.")]
    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
