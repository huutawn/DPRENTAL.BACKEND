using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dprental.Gateway.Infrastructure.Security.Middlewares;

namespace Dprental.Gateway.Infrastructure.Security.DelegatingHandlers;

/// <summary>
/// Loại bỏ các CORS response headers từ downstream service trước khi trả về client
/// để Gateway là nơi duy nhất áp dụng authoritative CORS policy.
/// </summary>
public sealed class GatewayCorsDelegatingHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        foreach (string header in GatewayCorsResponseHeaders.Names)
        {
            response.Headers.Remove(header);
        }

        return response;
    }
}
