using Dprental.AuthService.Common;

namespace Dprental.AuthService.Domain.Errors;

public static class ServicePrincipalErrors
{
    /// <summary>Sai client_id, sai secret, hoặc principal bị vô hiệu hóa — dùng chung 1 lỗi để không lộ lý do cụ thể.</summary>
    public static readonly Error InvalidClient =
        Error.Unauthorized("ServicePrincipal.InvalidClient", "client_id hoặc client_secret không hợp lệ.");

    public static readonly Error InvalidScope =
        Error.Forbidden("ServicePrincipal.InvalidScope", "Service này không được cấp scope/audience đã yêu cầu.");
}
