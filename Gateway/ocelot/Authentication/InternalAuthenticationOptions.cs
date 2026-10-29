namespace Dprental.Gateway.Authentication;

/// <summary>Cấu hình khóa dùng khi Gateway gọi các backend nội bộ; không ghi giá trị khóa vào log.</summary>
public sealed class InternalAuthenticationOptions
{
    public const string SectionName = "InternalAuthentication";

    public string ApiKey { get; set; } = string.Empty;
}
