using System.Security.Cryptography;
using System.Text;

namespace Dprental.MediaService.Api.Security;

/// <summary>
/// Giao diện xác thực API key dùng chung cho các API nội bộ.
/// </summary>
public interface IApiKeyValidator
{
    /// <summary>
    /// Kiểm tra tính hợp lệ của key được cung cấp qua so sánh constant-time.
    /// </summary>
    bool IsValid(string? candidateKey);
}

/// <summary>
/// Triển khai xác thực API key với so sánh an toàn constant-time chống timing attack.
/// </summary>
public sealed class ApiKeyValidator(string configuredKey) : IApiKeyValidator
{
    private readonly string _configuredKey = configuredKey ?? string.Empty;

    public bool IsValid(string? candidateKey)
    {
        if (string.IsNullOrWhiteSpace(candidateKey) || string.IsNullOrWhiteSpace(_configuredKey))
        {
            return false;
        }

        byte[] candidateBytes = Encoding.UTF8.GetBytes(candidateKey);
        byte[] configuredBytes = Encoding.UTF8.GetBytes(_configuredKey);

        return CryptographicOperations.FixedTimeEquals(candidateBytes, configuredBytes);
    }
}
