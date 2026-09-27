namespace Dprental.MediaService.Application.Policies;

public sealed class UploadSettings
{
    public int SessionLifetimeMinutes { get; set; } = 15;
}
