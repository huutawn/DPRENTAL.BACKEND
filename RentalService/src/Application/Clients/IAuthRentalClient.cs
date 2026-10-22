using Dprental.RentalService.Common;

namespace Dprental.RentalService.Application.Clients;

public sealed record AuthUserSummaryDto(Guid Id, bool IsProfileCompleted);

public interface IAuthRentalClient
{
    Task<Result<AuthUserSummaryDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
