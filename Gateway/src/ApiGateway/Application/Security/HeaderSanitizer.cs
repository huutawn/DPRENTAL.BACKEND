using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Cài đặt quy tắc làm sạch tiêu đề HTTP nhằm bảo vệ hệ thống trước các hành vi giả mạo danh tính từ phía client.
/// </summary>
public sealed class HeaderSanitizer : IHeaderSanitizer
{
    private static readonly Action<ILogger, string, Exception?> LogUntrustedHeadersRemoved = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "UntrustedHeadersRemoved"),
        "Đã phát hiện và loại bỏ các header nhạy cảm do client tự ý gửi lên: {RemovedHeaders}.");

    private readonly ILogger<HeaderSanitizer> logger;

    /// <summary>
    /// Khởi tạo bộ làm sạch tiêu đề với logger để ghi nhận các trường hợp phát hiện header bất thường.
    /// </summary>
    /// <param name="logger">Logger ghi log chẩn đoán.</param>
    public HeaderSanitizer(ILogger<HeaderSanitizer> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool ContainsUntrustedHeaders(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (string untrustedHeader in GatewayHeaderConstants.UntrustedClientHeaders)
        {
            if (headers.ContainsKey(untrustedHeader))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SanitizeInboundHeaders(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        List<string> removedHeaders = new();

        foreach (string untrustedHeader in GatewayHeaderConstants.UntrustedClientHeaders)
        {
            if (headers.Remove(untrustedHeader))
            {
                removedHeaders.Add(untrustedHeader);
            }
        }

        if (removedHeaders.Count > 0)
        {
            LogUntrustedHeadersRemoved(logger, string.Join(", ", removedHeaders), null);
        }

        return removedHeaders;
    }
}
