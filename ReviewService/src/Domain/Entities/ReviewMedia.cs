namespace Dprental.ReviewService.Domain.Entities;

/// <summary>
/// Liên kết hình ảnh đính kèm với một bài đánh giá.
/// </summary>
public sealed class ReviewMedia
{
    public Guid Id { get; private set; }
    public Guid ReviewId { get; private set; }
    public Guid MediaId { get; private set; }
    public string MediaType { get; private set; } = "image";
    public DateTimeOffset CreatedAt { get; private set; }

    private ReviewMedia() { }

    public ReviewMedia(
        Guid id,
        Guid reviewId,
        Guid mediaId,
        string mediaType,
        DateTimeOffset createdAt)
    {
        Id = id;
        ReviewId = reviewId;
        MediaId = mediaId;
        MediaType = mediaType ?? "image";
        CreatedAt = createdAt.ToUniversalTime();
    }
}
