using System.Security.Claims;
using Dprental.Gateway.Domain.Security;
using Microsoft.AspNetCore.Http;

namespace Dprental.Gateway.Application.Security;

/// <summary>
/// Giao diện trích xuất và chuẩn hóa ngữ cảnh danh tính đáng tin cậy (Identity Context)
/// từ ClaimsPrincipal đã được xác thực qua JWT.
/// </summary>
public interface IIdentityExtractor
{
    /// <summary>
    /// Trích xuất ngữ cảnh danh tính từ ClaimsPrincipal và HttpContext hiện tại.
    /// </summary>
    /// <param name="principal">Thông tin định danh và tập hợp claims của request.</param>
    /// <param name="httpContext">Ngữ cảnh HTTP hiện tại.</param>
    /// <returns>Đối tượng IdentityContext chứa thông tin danh tính đã được kiểm chứng.</returns>
    IdentityContext Extract(ClaimsPrincipal? principal, HttpContext httpContext);
}
