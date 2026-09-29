using System;
using System.Collections.Generic;
using System.Text;

namespace AuthService.Application.DTOs.Auth
{
    public record RoleDto(Guid Id, string Name, string? Description);
}
