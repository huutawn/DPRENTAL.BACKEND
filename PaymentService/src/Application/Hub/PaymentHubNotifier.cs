#pragma warning disable CA1848
using Dprental.PaymentService.Application.Hub.Contracts;
using Dprental.PaymentService.Application.Hub.Groups;
using Dprental.PaymentService.Application.Hub.Options;
using Dprental.PaymentService.Common;
using Dprental.PaymentService.Domain.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Hub;

public sealed class PaymentHubNotifier(
    IHubContext<PaymentHub> hubContext,
    PaymentHubGroups groups,
    IOptions<PaymentHubOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentHubNotifier> logger) : IPaymentHubNotifier
{
    private readonly IHubContext<PaymentHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly PaymentHubGroups _groups = groups ?? throw new ArgumentNullException(nameof(groups));
    private readonly PaymentHubOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<PaymentHubNotifier> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<Result> SendToUserAsync<T>(
        Guid userId, string eventName, T payload, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return Error.Validation("PaymentHub.InvalidUserId", "User ID cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return Error.Validation("PaymentHub.InvalidEventName", "Event name cannot be empty.");
        }

        var envelope = new PaymentHubEnvelope<T>(Guid.NewGuid(), 1, _timeProvider.GetUtcNow(), payload);
        using var cts = CreateSendTimeoutToken(cancellationToken);
        try
        {
            await _hubContext.Clients.User(userId.ToString("D")).SendAsync(eventName, envelope, cts.Token);
            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("SignalR push timed out sending {Event} to user {UserId}.", eventName, userId);
            return Error.Failure("PaymentHub.SendTimeout", "SignalR send timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR push failed sending {Event} to user {UserId}.", eventName, userId);
            return Error.Failure("PaymentHub.DeliveryFailed", ex.Message);
        }
    }

    public async Task<Result> SendToUsersAsync<T>(
        IReadOnlyCollection<Guid> userIds, string eventName, T payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return Error.Validation("PaymentHub.InvalidEventName", "Event name cannot be empty.");
        }

        var distinct = userIds.Where(u => u != Guid.Empty).Distinct().ToList();
        if (distinct.Count > _options.MaxUsersPerSend)
        {
            return Error.Validation("PaymentHub.MaxUsersPerSendExceeded",
                $"Cannot send to more than {_options.MaxUsersPerSend} users in a single operation.");
        }

        if (distinct.Count == 0)
        {
            return Result.Success();
        }

        var envelope = new PaymentHubEnvelope<T>(Guid.NewGuid(), 1, _timeProvider.GetUtcNow(), payload);
        var userStrings = distinct.Select(u => u.ToString("D")).ToList();
        using var cts = CreateSendTimeoutToken(cancellationToken);
        try
        {
            await _hubContext.Clients.Users(userStrings).SendAsync(eventName, envelope, cts.Token);
            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("SignalR push timed out sending {Event} to {Count} users.", eventName, distinct.Count);
            return Error.Failure("PaymentHub.SendTimeout", "SignalR send timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR push failed sending {Event} to {Count} users.", eventName, distinct.Count);
            return Error.Failure("PaymentHub.DeliveryFailed", ex.Message);
        }
    }

    public async Task<Result> SendToGroupAsync<T>(
        string groupName, string eventName, T payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return Error.Validation("PaymentHub.InvalidGroupName", "Group name cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return Error.Validation("PaymentHub.InvalidEventName", "Event name cannot be empty.");
        }

        var envelope = new PaymentHubEnvelope<T>(Guid.NewGuid(), 1, _timeProvider.GetUtcNow(), payload);
        using var cts = CreateSendTimeoutToken(cancellationToken);
        try
        {
            await _hubContext.Clients.Group(groupName).SendAsync(eventName, envelope, cts.Token);
            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("SignalR push timed out sending {Event} to group {Group}.", eventName, groupName);
            return Error.Failure("PaymentHub.SendTimeout", "SignalR send timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR push failed sending {Event} to group {Group}.", eventName, groupName);
            return Error.Failure("PaymentHub.DeliveryFailed", ex.Message);
        }
    }

    public async Task<Result> SetGroupUsersAsync(
        string groupName, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var groupResult = _groups.SetGroupUsers(groupName, userIds, out var toAdd, out var toRemove);
        if (groupResult.IsFailure)
        {
            return groupResult;
        }

        using var cts = CreateSendTimeoutToken(cancellationToken);
        foreach (var connId in toRemove)
        {
            try
            {
                await _hubContext.Groups.RemoveFromGroupAsync(connId, groupName, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove connection {ConnectionId} from group {Group}.", connId, groupName);
            }
        }

        foreach (var connId in toAdd)
        {
            try
            {
                await _hubContext.Groups.AddToGroupAsync(connId, groupName, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add connection {ConnectionId} to group {Group}.", connId, groupName);
            }
        }

        return Result.Success();
    }

    public async Task<Result> RemoveGroupAsync(
        string groupName, CancellationToken cancellationToken = default)
    {
        var groupResult = _groups.RemoveGroup(groupName, out var toRemove);
        if (groupResult.IsFailure)
        {
            return groupResult;
        }

        using var cts = CreateSendTimeoutToken(cancellationToken);
        foreach (var connId in toRemove)
        {
            try
            {
                await _hubContext.Groups.RemoveFromGroupAsync(connId, groupName, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove connection {ConnectionId} from group {Group}.", connId, groupName);
            }
        }

        return Result.Success();
    }

    public async Task<Result> NotifyPaymentCompletedAsync(
        Guid userId, Guid eventId, Invoice invoice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (userId == Guid.Empty)
        {
            return Error.Validation("PaymentHub.InvalidUserId", "Recipient user ID cannot be empty.");
        }
        if (eventId == Guid.Empty)
        {
            return Error.Validation("PaymentHub.InvalidEventId", "Event ID cannot be empty.");
        }

        var payload = new PaymentCompletedPayload(
            invoice.InvoiceRef,
            invoice.RentalOrderId,
            InvoiceStatus.Paid.ToString().ToUpperInvariant(),
            invoice.TotalAmount,
            invoice.Currency,
            invoice.PaidAt ?? invoice.CreatedAt,
            invoice.Revision);

        var envelope = new PaymentHubEnvelope<PaymentCompletedPayload>(eventId, 1, _timeProvider.GetUtcNow(), payload);

        using var cts = CreateSendTimeoutToken(cancellationToken);
        try
        {
            await _hubContext.Clients.User(userId.ToString("D"))
                .SendAsync(PaymentHubEventNames.PaymentCompleted, envelope, cts.Token);
            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("SignalR push timed out sending PaymentCompleted for invoice {InvoiceRef} to user {UserId}.",
                invoice.InvoiceRef, userId);
            return Error.Failure("PaymentHub.SendTimeout", "SignalR send timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignalR push failed sending PaymentCompleted for invoice {InvoiceRef} to user {UserId}.",
                invoice.InvoiceRef, userId);
            return Error.Failure("PaymentHub.DeliveryFailed", ex.Message);
        }
    }

    private CancellationTokenSource CreateSendTimeoutToken(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.SendTimeoutSeconds, 1, 10));
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }
}
