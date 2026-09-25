using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Domain.DTOs.User;

public sealed record UpdateUserProfileRequest(
    [Required, StringLength(100, MinimumLength = 1)] string FullName,
    [RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string? PhoneNumber = null,
    [OptionalUrl, StringLength(500)] string? AvatarUrl = null
);