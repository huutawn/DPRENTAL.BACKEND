namespace Dprental.PaymentService.Api.Security;

/// <summary>
/// Cấu hình xác thực nội bộ qua API Key.
/// </summary>
public sealed class InternalAuthenticationOptions
{
    /// <summary>Tên section cấu hình.</summary>
    public const string SectionName = "InternalAuthentication";

    /// <summary>Khóa API nội bộ.</summary>
    public string ApiKey { get; set; } = string.Empty;
}
