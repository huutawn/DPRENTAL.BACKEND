using Dprental.PaymentService.Application.Hub.Options;
using Dprental.PaymentService.Common;
using Microsoft.Extensions.Options;

namespace Dprental.PaymentService.Application.Hub.Groups;

public sealed class PaymentHubGroups(IOptions<PaymentHubOptions> options)
{
    private readonly PaymentHubOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly object _lock = new();

    // connectionId -> userId
    private readonly Dictionary<string, Guid> _connectionToUser = new(StringComparer.Ordinal);

    // userId -> set of active connectionIds
    private readonly Dictionary<Guid, HashSet<string>> _userConnections = [];

    // groupName -> set of member userIds
    private readonly Dictionary<string, HashSet<Guid>> _groupUsers = new(StringComparer.Ordinal);

    public int ActiveConnectionsCount
    {
        get
        {
            lock (_lock)
            {
                return _connectionToUser.Count;
            }
        }
    }

    public int GroupsCount
    {
        get
        {
            lock (_lock)
            {
                return _groupUsers.Count;
            }
        }
    }

    public Result TryRegisterConnection(string connectionId, Guid userId, out IReadOnlyList<string> groupsToJoin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (userId == Guid.Empty)
        {
            groupsToJoin = [];
            return Error.Validation("PaymentHub.InvalidUserId", "User ID cannot be empty.");
        }

        lock (_lock)
        {
            if (!_connectionToUser.ContainsKey(connectionId) && _connectionToUser.Count >= _options.MaxConnections)
            {
                groupsToJoin = [];
                return Error.Conflict("PaymentHub.MaxConnectionsExceeded", "Hub connection limit reached.");
            }

            _connectionToUser[connectionId] = userId;

            if (!_userConnections.TryGetValue(userId, out var conns))
            {
                conns = new HashSet<string>(StringComparer.Ordinal);
                _userConnections[userId] = conns;
            }
            conns.Add(connectionId);

            var toJoin = new List<string>();
            foreach (var (grp, members) in _groupUsers)
            {
                if (members.Contains(userId))
                {
                    toJoin.Add(grp);
                }
            }
            groupsToJoin = toJoin;
            return Result.Success();
        }
    }

    public void UnregisterConnection(string connectionId, out Guid? userId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            userId = null;
            return;
        }

        lock (_lock)
        {
            if (_connectionToUser.Remove(connectionId, out var uid))
            {
                userId = uid;
                if (_userConnections.TryGetValue(uid, out var conns))
                {
                    conns.Remove(connectionId);
                    if (conns.Count == 0)
                    {
                        _userConnections.Remove(uid);
                    }
                }
            }
            else
            {
                userId = null;
            }
        }
    }

    public Result SetGroupUsers(
        string groupName,
        IReadOnlyCollection<Guid> userIds,
        out IReadOnlyList<string> connectionsToAdd,
        out IReadOnlyList<string> connectionsToRemove)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            connectionsToAdd = [];
            connectionsToRemove = [];
            return Error.Validation("PaymentHub.InvalidGroupName", "Group name cannot be empty.");
        }

        var distinctUsers = userIds?.Where(u => u != Guid.Empty).Distinct().ToList() ?? [];

        if (distinctUsers.Count > _options.MaxUsersPerGroup)
        {
            connectionsToAdd = [];
            connectionsToRemove = [];
            return Error.Validation("PaymentHub.MaxGroupUsersExceeded",
                $"Group cannot have more than {_options.MaxUsersPerGroup} users.");
        }

        lock (_lock)
        {
            bool isExisting = _groupUsers.TryGetValue(groupName, out var currentMembers);
            if (!isExisting && distinctUsers.Count > 0 && _groupUsers.Count >= _options.MaxGroups)
            {
                connectionsToAdd = [];
                connectionsToRemove = [];
                return Error.Conflict("PaymentHub.MaxGroupsExceeded",
                    $"Cannot create more than {_options.MaxGroups} groups.");
            }

            if (distinctUsers.Count == 0)
            {
                // Remove group
                if (isExisting && currentMembers != null)
                {
                    var removeList = new List<string>();
                    foreach (var u in currentMembers)
                    {
                        if (_userConnections.TryGetValue(u, out var conns))
                        {
                            removeList.AddRange(conns);
                        }
                    }
                    _groupUsers.Remove(groupName);
                    connectionsToAdd = [];
                    connectionsToRemove = removeList;
                    return Result.Success();
                }

                connectionsToAdd = [];
                connectionsToRemove = [];
                return Result.Success();
            }

            currentMembers ??= [];
            var newSet = new HashSet<Guid>(distinctUsers);

            var removedUsers = currentMembers.Except(newSet).ToList();
            var addedUsers = newSet.Except(currentMembers).ToList();

            var toRemove = new List<string>();
            foreach (var ru in removedUsers)
            {
                if (_userConnections.TryGetValue(ru, out var conns))
                {
                    toRemove.AddRange(conns);
                }
            }

            var toAdd = new List<string>();
            foreach (var au in addedUsers)
            {
                if (_userConnections.TryGetValue(au, out var conns))
                {
                    toAdd.AddRange(conns);
                }
            }

            _groupUsers[groupName] = newSet;
            connectionsToAdd = toAdd;
            connectionsToRemove = toRemove;
            return Result.Success();
        }
    }

    public Result RemoveGroup(string groupName, out IReadOnlyList<string> connectionsToRemove)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            connectionsToRemove = [];
            return Error.Validation("PaymentHub.InvalidGroupName", "Group name cannot be empty.");
        }

        lock (_lock)
        {
            if (_groupUsers.Remove(groupName, out var members))
            {
                var toRemove = new List<string>();
                foreach (var u in members)
                {
                    if (_userConnections.TryGetValue(u, out var conns))
                    {
                        toRemove.AddRange(conns);
                    }
                }
                connectionsToRemove = toRemove;
                return Result.Success();
            }

            connectionsToRemove = [];
            return Result.Success();
        }
    }
}
