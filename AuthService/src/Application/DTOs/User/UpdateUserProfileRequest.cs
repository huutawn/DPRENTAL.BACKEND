using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Validation;

namespace Dprental.AuthService.Domain.DTOs.User;

public sealed record UpdateUserProfileRequest(
    [Required, StringLength(100, MinimumLength = 1)] string FullName,
    [RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string? PhoneNumber = null,
    DateOnly? DateOfBirth = null,
    [RegularExpression(RegistrationPatterns.CitizenId, ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")] string? CitizenId = null,
    [NonEmptyGuid] Guid? AvatarMediaId = null,
    bool RemoveAvatar = false,
    [OptionalUrl, StringLength(500)] string? AvatarUrl = null
);
