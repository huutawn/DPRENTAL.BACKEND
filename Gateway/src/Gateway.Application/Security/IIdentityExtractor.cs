using System.Security.Claims;
using Dprental.Gateway.Domain.Security;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Giao diện trích xuất và chuẩn hóa ngữ cảnh danh tính đáng tin cậy (Identity Context)
/// từ thông tin xác thực ClaimsPrincipal và correlation ID.
/// </summary>
public interface IIdentityExtractor
{
    /// <summary>
    /// Trích xuất ngữ cảnh danh tính từ ClaimsPrincipal và mã tương quan phân tán (correlation ID).
    /// </summary>
    /// <param name="principal">Thông tin định danh người dùng đã xác thực.</param>
    /// <param name="correlationId">Mã tương quan phân tán cho request.</param>
    /// <returns>Đối tượng IdentityContext chứa thông tin danh tính đã được kiểm chứng.</returns>
    IdentityContext Extract(ClaimsPrincipal? principal, string? correlationId);
}
