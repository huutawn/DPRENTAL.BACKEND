namespace Dprental.MediaService.Api.Configuration;

/// <summary>
/// Mở rộng hỗ trợ xác thực các cấu hình bắt buộc trước khi dịch vụ Media khởi động.
/// </summary>
public static class ConfigurationValidationExtensions
{
    /// <summary>
    /// Kiểm tra các cấu hình bắt buộc. Ném ra InvalidOperationException nếu thiếu bất kỳ cấu hình nào.
    /// </summary>
    /// <param name="configuration">Cấu hình ứng dụng.</param>
    public static void ValidateRequiredConfiguration(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var postgres = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(postgres))
        {
            throw new InvalidOperationException("Thiếu cấu hình bắt buộc: ConnectionStrings:Postgres.");
        }

        var missingStorageKeys = new List<string>();

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:Endpoint"]))
        {
            missingStorageKeys.Add("ObjectStorage:Endpoint");
        }

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:Bucket"]))
        {
            missingStorageKeys.Add("ObjectStorage:Bucket");
        }

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:AccessKey"]))
        {
            missingStorageKeys.Add("ObjectStorage:AccessKey");
        }

        if (string.IsNullOrWhiteSpace(configuration["ObjectStorage:SecretKey"]))
        {
            missingStorageKeys.Add("ObjectStorage:SecretKey");
        }

        if (missingStorageKeys.Count > 0)
        {
            throw new InvalidOperationException(
                $"Thiếu cấu hình ObjectStorage bắt buộc: {string.Join(", ", missingStorageKeys)}.");
        }
    }
}
