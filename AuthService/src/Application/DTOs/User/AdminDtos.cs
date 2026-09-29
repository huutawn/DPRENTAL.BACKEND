namespace Dprental.AuthService.Application.DTOs;

public class UserFilterRequest
{
    public string? Keyword { get; set; }
    public string? Type { get; set; }
    public int PageIndex { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class CreateAdminRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Type { get; set; }
    public List<Guid> RoleIds { get; set; } = [];
}

public class UpdateAdminRequest
{
    public string FullName { get; set; } = string.Empty;
    public string? Type { get; set; }
    public List<Guid> RoleIds { get; set; } = [];
}

public class UserResponse
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Type { get; set; }

    public List<string> Roles { get; set; } = [];
}