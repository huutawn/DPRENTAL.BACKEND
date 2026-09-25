namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Cấu hình xác thực JWT cho MediaService.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? SecretKey { get; set; }
    public string? Authority { get; set; }
}
