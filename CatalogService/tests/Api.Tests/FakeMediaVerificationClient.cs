using System.Collections.Concurrent;
using Dprental.CatalogService.Application.Clients;

namespace Dprental.CatalogService.Api.Tests;

/// <summary>Thay Media Service trong test: mặc định mọi media đều Ready, có thể gán kết quả riêng cho từng MediaId.</summary>
public sealed class FakeMediaVerificationClient : IMediaVerificationClient
{
    private readonly ConcurrentDictionary<Guid, MediaVerificationStatus> statuses = new();
    private readonly ConcurrentQueue<Guid> verified = new();

    public MediaVerificationStatus DefaultStatus { get; set; } = MediaVerificationStatus.Ready;

    /// <summary>Các MediaId đã được gọi verify, theo thứ tự gọi.</summary>
    public IReadOnlyCollection<Guid> VerifiedMediaIds => verified;

    public void ClearVerifiedMediaIds() => verified.Clear();

    public void SetStatus(Guid mediaId, MediaVerificationStatus status) => statuses[mediaId] = status;

    public Task<MediaVerificationStatus> VerifyAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        verified.Enqueue(mediaId);
        return Task.FromResult(statuses.TryGetValue(mediaId, out MediaVerificationStatus status) ? status : DefaultStatus);
    }
}
