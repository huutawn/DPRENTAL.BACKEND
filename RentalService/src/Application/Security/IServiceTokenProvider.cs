using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Security;

public interface IServiceTokenProvider
{
    Task<Result<string>> GetServiceTokenAsync(string audience, string scope, CancellationToken cancellationToken = default);
}
