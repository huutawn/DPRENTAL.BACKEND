namespace Dprental.PaymentService.Api.Security;

public sealed class ForwardedHeadersSecurityOptions
{
    public const string SectionName = "Security:ForwardedHeaders";

    public List<string> KnownProxies { get; set; } = [];
    public List<string> KnownNetworks { get; set; } = [];
}
