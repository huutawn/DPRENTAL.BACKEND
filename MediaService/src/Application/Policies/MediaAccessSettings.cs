namespace Dprental.MediaService.Application.Policies;

public sealed class MediaAccessSettings
{
    public int ReadUrlExpirySeconds { get; set; } = 300;
}
