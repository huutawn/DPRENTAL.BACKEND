namespace Dprental.AnalyticsService.Application.Hub.Security;

public static class AnalyticsHubSecurityConstants
{
    public const string PolicyName = "AnalyticsHubClientPolicy";

    // Cùng tên cookie với PaymentService để FE dùng chung một cách gắn token
    public const string AccessTokenCookieName = "accessToken";

    // Admin vào nhóm này lúc kết nối; nhận mọi thông báo thay đổi số liệu
    public const string AdminGroup = "admins";
}
