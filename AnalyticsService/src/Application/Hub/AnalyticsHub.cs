using Dprental.AnalyticsService.Application.Hub.Security;
using Dprental.AnalyticsService.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace Dprental.AnalyticsService.Application.Hub;

/// <summary>
/// Cửa kết nối SignalR cho dashboard. Client chỉ NHẬN tin, không gọi hàm nào lên server.
/// Admin vào nhóm admins; NCC nhận tin theo user (sub) của chính mình.
/// </summary>
[Authorize(Policy = AnalyticsHubSecurityConstants.PolicyName)]
public sealed class AnalyticsHub : Microsoft.AspNetCore.SignalR.Hub
{
    public override async Task OnConnectedAsync()
    {
        // UserIdProvider trả null khi token không hợp lệ cho Hub → ngắt kết nối
        if (string.IsNullOrEmpty(Context.UserIdentifier) || Context.User is null)
        {
            Context.Abort();
            return;
        }

        if (Context.User.IsPlatformAdmin())
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AnalyticsHubSecurityConstants.AdminGroup);
        }

        await base.OnConnectedAsync();
    }
}
