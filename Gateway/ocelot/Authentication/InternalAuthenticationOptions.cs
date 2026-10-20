namespace Dprental.Gateway.Authentication;

public sealed class InternalAuthenticationOptions
{
    public const string SectionName = "InternalAuthentication";

    public string ApiKey { get; set; } = string.Empty;
}
