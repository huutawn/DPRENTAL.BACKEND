namespace Dprental.MediaService.Domain.Processing;

public interface IImageProcessor
{
    Task<IReadOnlyList<ProcessedImageVariant>> GenerateVariantsAsync(
        Stream sourceStream,
        string contentType,
        IReadOnlyList<ImageVariantSpec> specs,
        CancellationToken cancellationToken = default);
}
