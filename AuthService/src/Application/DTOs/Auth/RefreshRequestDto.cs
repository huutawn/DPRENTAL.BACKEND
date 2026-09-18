namespace AuthService.Application.DTOs.Auth;

public sealed class RefreshRequestDto
{
    public string RefreshToken { get; set; } = string.Empty;
}
