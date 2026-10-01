using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Dprental.AuthService.Application.Validation;

namespace AuthService.Application.DTOs.User
{
    public class CreateUserRequest
    {
        [Required, StrictEmail, StringLength(254)]
        public string Email { get; set; } = string.Empty;
        [Required, StringLength(PasswordPolicy.MaxLength, MinimumLength = PasswordPolicy.MinLength), NotCommonPassword]
        public string Password { get; set; } = string.Empty;
        [Required]
        public string FullName { get; set; } = string.Empty;
        public string? Type { get; set; }
        public List<Guid> RoleIds { get; set; } = []; // Hứng danh sách role gán lúc tạo
    }
}
