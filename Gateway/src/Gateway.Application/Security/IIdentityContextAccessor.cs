using Dprental.Gateway.Domain.Security;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Cung cấp cơ chế truy cập ngữ cảnh danh tính đáng tin cậy của request hiện tại
/// trong phạm vi xử lý của Gateway.
/// </summary>
public interface IIdentityContextAccessor
{
    /// <summary>
    /// Lấy ngữ cảnh danh tính của request đang được xử lý.
    /// </summary>
    IdentityContext Current { get; }

    /// <summary>
    /// Thiết lập ngữ cảnh danh tính đã được trích xuất cho request hiện tại.
    /// </summary>
    /// <param name="context">Ngữ cảnh danh tính mới.</param>
    void SetCurrent(IdentityContext context);
}
