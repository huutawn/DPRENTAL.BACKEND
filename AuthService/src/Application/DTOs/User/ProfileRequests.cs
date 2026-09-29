using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Validation;

namespace Dprental.AuthService.Application.DTOs.User;

/// <summary>Yêu cầu hoàn thiện hồ sơ lần đầu (CCCD, SĐT, ngày sinh).</summary>
public sealed record CompleteProfileRequest(
    [Required, RegularExpression(RegistrationPatterns.CitizenId, ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")] string CitizenId,
    [Required, RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string PhoneNumber,
    [Required] DateOnly? DateOfBirth,
    [StringLength(100, MinimumLength = 1)] string? FullName = null,
    [NonEmptyGuid] Guid? AvatarMediaId = null);

/// <summary>Yêu cầu cập nhật hồ sơ cá nhân sau khi đã hoàn thiện.</summary>
public sealed record UpdateProfileRequest(
    [StringLength(100, MinimumLength = 1)] string? FullName = null,
    [RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")] string? PhoneNumber = null,
    DateOnly? DateOfBirth = null,
    [NonEmptyGuid] Guid? AvatarMediaId = null,
    bool RemoveAvatar = false);
