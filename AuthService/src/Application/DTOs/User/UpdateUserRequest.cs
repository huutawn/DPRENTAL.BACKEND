using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dprental.AuthService.Application.DTOs.Auth;
using Dprental.AuthService.Application.Validation;

namespace AuthService.Application.DTOs.User;

public class UpdateUserRequest
{
    [Required(ErrorMessage = "Họ và tên không được để trống."), StringLength(100, MinimumLength = 1, ErrorMessage = "Họ và tên tối đa 100 ký tự.")]
    public string FullName { get; set; } = string.Empty;

    [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
    public string? Email { get; set; }

    [RegularExpression(RegistrationPatterns.PhoneNumber, ErrorMessage = "Số điện thoại không hợp lệ.")]
    public string? PhoneNumber { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    [RegularExpression(RegistrationPatterns.CitizenId, ErrorMessage = "Số CCCD phải gồm đúng 12 chữ số.")]
    public string? CitizenId { get; set; }

    [NonEmptyGuid]
    public Guid? AvatarMediaId { get; set; }

    public bool RemoveAvatar { get; set; }

    public bool? IsActive { get; set; }

    public string? Type { get; set; }

    public List<Guid>? RoleIds { get; set; }
}
