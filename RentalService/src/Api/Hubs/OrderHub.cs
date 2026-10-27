using Dprental.RentalService.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Dprental.RentalService.Api.Hubs;

/// <summary>
/// Hub thông báo invalidation cho Order của người thuê và đơn con của nhà cung cấp.
/// Chỉ hỗ trợ server push; client không thể gửi message hoặc tự quản lý membership.
/// </summary>
[Authorize(Policy = OrderHubSecurityConstants.PolicyName)]
public sealed partial class OrderHub(ILogger<OrderHub> logger) : Hub
{
    private readonly ILogger<OrderHub> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task OnConnectedAsync()
    {
        var caller = RentalCallerContext.FromClaimsPrincipal(Context.User);
        if (!caller.IsAuthenticated || caller.IsService || !caller.UserId.HasValue || caller.UserId.Value == Guid.Empty)
        {
            LogInvalidCaller(_logger, Context.ConnectionId);
            Context.Abort();
            return;
        }

        Guid userId = caller.UserId.Value;
        string renterGroup = OrderHubGroupNames.Renter(userId);
        string supplierGroup = OrderHubGroupNames.Supplier(userId);

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, renterGroup);
            await Groups.AddToGroupAsync(Context.ConnectionId, supplierGroup);
        }
        catch (Exception ex)
        {
            LogGroupJoinFailed(_logger, Context.ConnectionId, userId, ex);
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "OrderHub connection {ConnectionId} rejected: invalid or service caller identity.")]
    private static partial void LogInvalidCaller(ILogger logger, string connectionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Failed to auto-join order groups for connection {ConnectionId}, user {UserId}.")]
    private static partial void LogGroupJoinFailed(ILogger logger, string connectionId, Guid userId, Exception ex);
}
