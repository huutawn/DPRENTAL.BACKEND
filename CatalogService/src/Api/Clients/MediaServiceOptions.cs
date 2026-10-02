namespace Dprental.CatalogService.Api.Clients;

/// <summary>Cấu hình kết nối tới internal API của Media Service.</summary>
internal sealed class MediaServiceOptions
{
    internal const string SectionName = "MediaService";

    /// <summary>URL gốc của Media Service hoặc Gateway, ví dụ <c>http://media:8080</c>.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Service API key gửi qua header <c>X-Api-Key</c>. Chỉ nạp từ user-secrets hoặc biến môi trường.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Thời gian chờ tối đa cho một lần gọi verify.</summary>
    public int TimeoutSeconds { get; init; } = 5;
}
