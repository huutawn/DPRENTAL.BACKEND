using Microsoft.AspNetCore.Http;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Giao diện định nghĩa các quy tắc làm sạch tiêu đề HTTP (Header Sanitization),
/// loại bỏ các header nội bộ nhạy cảm do client tự ý gửi lên nhằm mục đích giả mạo danh tính.
/// </summary>
public interface IHeaderSanitizer
{
    /// <summary>
    /// Kiểm tra xem request có chứa bất kỳ tiêu đề không đáng tin cậy nào từ client hay không.
    /// </summary>
    /// <param name="headers">Tập hợp tiêu đề của request.</param>
    /// <returns>True nếu phát hiện có header không tin cậy, ngược lại là false.</returns>
    bool ContainsUntrustedHeaders(IHeaderDictionary headers);

    /// <summary>
    /// Loại bỏ toàn bộ các tiêu đề không tin cậy ra khỏi danh sách tiêu đề của request đến.
    /// </summary>
    /// <param name="headers">Tập hợp tiêu đề cần làm sạch.</param>
    /// <returns>Danh sách tên các tiêu đề đã bị loại bỏ.</returns>
    IReadOnlyList<string> SanitizeInboundHeaders(IHeaderDictionary headers);
}
