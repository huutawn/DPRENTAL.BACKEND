using Dprental.Gateway.Extensions.Authentication;
using Dprental.Gateway.Models.Authentication;
namespace Dprental.Gateway.Extensions.Health;

/// <summary>Nhãn phân loại health check để các endpoint chạy đúng nhóm kiểm tra.</summary>
internal static class GatewayHealthCheckTags
{
    internal const string Readiness = "ready";
}
