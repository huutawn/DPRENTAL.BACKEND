using Dprental.AuthService.Application.DTOs.Media;
using Dprental.AuthService.Application.Options;
using Dprental.AuthService.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.AuthService.Application.Clients;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1848:Use the LoggerMessage delegates", Justification = "Acceptable for client integration.")]
public sealed class MediaUrlResolver(
    IMediaServiceClient mediaServiceClient,
    IOptions<MediaServiceOptions> options,
    ILogger<MediaUrlResolver> logger) : IMediaUrlResolver
{
    private readonly MediaServiceOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public string? ResolvePublicUrl(Guid? mediaId)
    {
        if (mediaId is null || mediaId == Guid.Empty)
        {
            return null;
        }

        string gatewayUrl = _options.PublicGatewayUrl.TrimEnd('/');
        return $"{gatewayUrl}/api/v1/media/{mediaId.Value:D}/content";
    }

    public async Task<Result<MediaReadUrlDto>> ResolvePrivateAccessUrlAsync(Guid mediaId, CancellationToken cancellationToken = default)
    {
        if (mediaId == Guid.Empty)
        {
            return Result.Failure<MediaReadUrlDto>(AuthErrors.Media.NotFound);
        }

        Result<MediaReadUrlDto> result = await mediaServiceClient.CreatePrivateAccessUrlAsync(mediaId, cancellationToken);
        if (result.IsFailure)
        {
            logger.LogWarning("Failed to resolve private access URL for media {MediaId}: {ErrorCode}", mediaId, result.Error.Code);
            return Result.Failure<MediaReadUrlDto>(AuthErrors.Media.ServiceUnavailable);
        }

        return result;
    }
}
