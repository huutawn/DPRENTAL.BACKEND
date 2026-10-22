#pragma warning disable CA1848
using Dprental.PaymentService.Application.Hub.Groups;
using Dprental.PaymentService.Application.Hub.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Dprental.PaymentService.Application.Hub;

[Authorize(Policy = PaymentHubSecurityConstants.PolicyName)]
public sealed class PaymentHub(PaymentHubGroups groups, ILogger<PaymentHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly PaymentHubGroups _groups = groups ?? throw new ArgumentNullException(nameof(groups));
    private readonly ILogger<PaymentHub> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task OnConnectedAsync()
    {
        string? userIdStr = Context.UserIdentifier;
        if (!Guid.TryParse(userIdStr, out var userId) || userId == Guid.Empty)
        {
            _logger.LogWarning("Connection {ConnectionId} rejected: missing or invalid user identifier.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var regResult = _groups.TryRegisterConnection(Context.ConnectionId, userId, out var groupsToJoin);
        if (regResult.IsFailure)
        {
            _logger.LogWarning("Connection {ConnectionId} registration rejected: {Error}", Context.ConnectionId, regResult.Error.Message);
            Context.Abort();
            return;
        }

        foreach (var group in groupsToJoin)
        {
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, group);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-join group {Group} for connection {ConnectionId}.", group, Context.ConnectionId);
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _groups.UnregisterConnection(Context.ConnectionId, out _);
        await base.OnDisconnectedAsync(exception);
    }
}
