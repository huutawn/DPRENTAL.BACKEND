using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace AuthService.Application.DTOs.User
{
    public class UpdateUserRequest
    {
        [Required]
        public string FullName { get; set; } = string.Empty;
        public string? Type { get; set; }
        public List<Guid> RoleIds { get; set; } = []; // Hứng danh sách role cập nhật
    }
}
