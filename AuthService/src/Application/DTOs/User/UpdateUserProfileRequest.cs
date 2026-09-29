using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Validation;

namespace Dprental.AuthService.Domain.DTOs.User;

public sealed record UpdateUserProfileRequest(
    [Required, StringLength(100, MinimumLength = 1)] string FullName,
    [RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string? PhoneNumber = null,
    [NonEmptyGuid] Guid? AvatarMediaId = null,
    bool RemoveAvatar = false
);
