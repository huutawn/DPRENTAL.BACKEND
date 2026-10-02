using Dprental.AuthService.Application.Clients;
using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Application.IServices;
using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Domain.IRepositories;

namespace Dprental.AuthService.Api.Tests;

public sealed class FakeMediaServiceClient : IMediaServiceClient
{
    public Dictionary<Guid, MediaVerificationDto> Verifications { get; } = [];
    public Dictionary<Guid, MediaReadUrlDto> AccessUrls { get; } = [];
    public List<(Guid MediaId, string Visibility)> VisibilityCalls { get; } = [];
    public bool AutoVerify { get; set; }
    public Guid? DefaultUserId { get; set; }
    public string DefaultContentType { get; set; } = "image/png";

    public Error? VerifyError { get; set; }
    public Error? AccessError { get; set; }
    public Error? VisibilityError { get; set; }

    public Task<Result<MediaVerificationDto>> VerifyMediaAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (VerifyError is not null)
        {
            return Task.FromResult(Result.Failure<MediaVerificationDto>(VerifyError));
        }

        if (Verifications.TryGetValue(mediaId, out var dto))
        {
            return Task.FromResult(Result.Success(dto));
        }

        if (AutoVerify && DefaultUserId.HasValue)
        {
            return Task.FromResult(Result.Success(new MediaVerificationDto(
                mediaId,
                "Ready",
                DefaultUserId.Value.ToString("D"),
                "file.png",
                DefaultContentType,
                1024,
                DateTimeOffset.UtcNow)));
        }

        return Task.FromResult(Result.Failure<MediaVerificationDto>(AuthErrors.Media.NotFound));
    }

    public Task<Result<MediaReadUrlDto>> CreatePrivateAccessUrlAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (AccessError is not null)
        {
            return Task.FromResult(Result.Failure<MediaReadUrlDto>(AccessError));
        }

        if (AccessUrls.TryGetValue(mediaId, out var dto))
        {
            return Task.FromResult(Result.Success(dto));
        }

        var defaultDto = new MediaReadUrlDto(mediaId, $"https://media.dprental.vn/signed/{mediaId}", DateTimeOffset.UtcNow.AddMinutes(15));
        return Task.FromResult(Result.Success(defaultDto));
    }

    public Task<Result<MediaVisibilityDto>> SetVisibilityAsync(Guid mediaId, string visibility, CancellationToken cancellationToken = default)
    {
        VisibilityCalls.Add((mediaId, visibility));

        if (VisibilityError is not null)
        {
            return Task.FromResult(Result.Failure<MediaVisibilityDto>(VisibilityError));
        }

        return Task.FromResult(Result.Success(new MediaVisibilityDto(mediaId, visibility)));
    }
}

public sealed class FakeMediaUrlResolver : IMediaUrlResolver
{
    public static readonly FakeMediaUrlResolver Instance = new();

    public Error? AccessError { get; set; }

    public string? ResolvePublicUrl(Guid? mediaId)
    {
        if (mediaId is null || mediaId == Guid.Empty)
        {
            return null;
        }

        return $"http://localhost:5050/api/v1/media/{mediaId.Value:D}/content";
    }

    public Task<Result<MediaReadUrlDto>> ResolvePrivateAccessUrlAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (AccessError is not null)
        {
            return Task.FromResult(Result.Failure<MediaReadUrlDto>(AccessError));
        }

        var dto = new MediaReadUrlDto(mediaId, $"https://media.dprental.vn/signed/{mediaId}", DateTimeOffset.UtcNow.AddMinutes(15));
        return Task.FromResult(Result.Success(dto));
    }
}

public sealed class FakeMediaOutboxService : IMediaOutboxService
{
    public List<Guid> QueuedMediaIds { get; } = [];

    public Task QueuePublicVisibilityAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        QueuedMediaIds.Add(mediaId);
        return Task.CompletedTask;
    }

    public Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(QueuedMediaIds.Count);
}

public sealed class FakeMediaOutboxRepository : IMediaOutboxRepository
{
    public List<MediaOutboxMessage> Messages { get; } = [];

    public Task AddAsync(MediaOutboxMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaOutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var pending = Messages.Where(m =>
            ((m.Status == "Pending" && (m.NextRetryAtUtc is null || m.NextRetryAtUtc <= now)) ||
             (m.Status == "Processing" && m.NextRetryAtUtc <= now)) && m.RetryCount < 5)
            .Take(batchSize).ToList();
        foreach (var message in pending)
        {
            message.Status = "Processing";
            message.NextRetryAtUtc = now.AddMinutes(1);
        }
        return Task.FromResult<IReadOnlyList<MediaOutboxMessage>>(pending);
    }

    public Task UpdateAsync(MediaOutboxMessage message, CancellationToken cancellationToken = default)
    {
        var index = Messages.FindIndex(m => m.Id == message.Id);
        if (index >= 0)
        {
            Messages[index] = message;
        }
        else
        {
            Messages.Add(message);
        }
        return Task.CompletedTask;
    }
}

public sealed class FakeAuditLogRepository : IAuditLogRepository
{
    public List<AuditLog> Logs { get; } = [];

    public Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default)
    {
        Logs.Add(auditLog);
        return Task.CompletedTask;
    }

    public Task<PagedResult<AuditLog>> GetOrganizationHistoryAsync(
        Guid organizationId,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        string target = organizationId.ToString();
        List<AuditLog> list = Logs
            .Where(l => l.Target == target && l.Action.StartsWith("Organization_", StringComparison.Ordinal))
            .OrderByDescending(l => l.Timestamp)
            .ThenByDescending(l => l.Id)
            .ToList();
        List<AuditLog> paged = list.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
        return Task.FromResult(new PagedResult<AuditLog>(paged, request.Page, request.PageSize, list.Count));
    }
}
