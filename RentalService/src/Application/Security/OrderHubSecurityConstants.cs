namespace Dprental.RentalService.Application.Security;

/// <summary>
/// Các hằng số bảo mật cho OrderHub.
/// </summary>
public static class OrderHubSecurityConstants
{
    public const string PolicyName = "OrderHubPolicy";
    public const string AccessTokenCookieName = "accessToken";
    public const string HubEndpoint = "/api/v1/rental/hubs/orders";
}
