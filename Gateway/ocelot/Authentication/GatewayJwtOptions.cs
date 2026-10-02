namespace Dprental.Gateway.Authentication;

/// <summary>JWT trust configuration owned by the Gateway.</summary>
internal sealed class GatewayJwtOptions
{
    internal const string SectionName = "Gateway:Authentication:Jwt";

    /// <summary>Expected issuer claim of access tokens issued by AuthService.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Expected audience claim of access tokens accepted by the Gateway.</summary>
    public string Audience { get; init; } = string.Empty;

    /// <summary>Expected audience claim of service tokens accepted for InventoryService routes.</summary>
    public string InventoryAudience { get; init; } = "inventory-api";

    /// <summary>AuthService endpoint exposing only public JSON Web Keys.</summary>
    public string JwksUri { get; init; } = string.Empty;
}
