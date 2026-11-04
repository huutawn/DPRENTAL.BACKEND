using Dprental.NotificationService.Api.Security;
using Dprental.NotificationService.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dprental.NotificationService.Api.Hubs;

[Authorize]
public sealed class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task OnConnectedAsync()
    {
        var userResult = Context.User?.TryGetHumanUserId()
            ?? Result.Failure<Guid>(Error.Unauthorized("UNAUTHORIZED", "Người dùng chưa xác thực hoặc thiếu Bearer token."));

        if (userResult.IsFailure)
        {
            _logger.LogWarning("Từ chối kết nối SignalR: {Code} - {Message}", userResult.Error.Code, userResult.Error.Message);
            try
            {
                var httpContext = Context.GetHttpContext();
                if (httpContext != null)
                {
                    httpContext.Response.StatusCode = userResult.Error.Type == ErrorType.Forbidden
                        ? StatusCodes.Status403Forbidden
                        : StatusCodes.Status401Unauthorized;
                }
            }
            catch
            {
                // Fallback nếu HttpContext không khả dụng
            }

            Context.Abort();

            if (userResult.Error.Type == ErrorType.Forbidden)
            {
                throw new HubException("403 Forbidden: Service principal (u: svc) is not allowed to access SignalR hub.");
            }

            throw new HubException($"401 Unauthorized: {userResult.Error.Message}");
        }

        var userId = userResult.Value;
        var userGroupName = $"user:{userId}";

        await Groups.AddToGroupAsync(Context.ConnectionId, userGroupName);
        _logger.LogInformation("SignalR ConnectionId={ConnectionId} đã tham gia nhóm riêng {Group}", Context.ConnectionId, userGroupName);

        await base.OnConnectedAsync();
    }
}
