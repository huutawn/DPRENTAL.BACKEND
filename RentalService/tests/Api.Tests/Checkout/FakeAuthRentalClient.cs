using System;
using System.Threading;
using System.Threading.Tasks;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Common;

namespace Dprental.RentalService.Api.Tests.Checkout;

public sealed class FakeAuthRentalClient : IAuthRentalClient
{
    public Guid? UserId { get; set; }
    public bool IsProfileCompleted { get; set; } = true;
    public Error? Failure { get; set; }
    public int CallCount { get; private set; }

    public Task<Result<AuthUserSummaryDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        if (Failure is not null)
        {
            return Task.FromResult<Result<AuthUserSummaryDto>>(Failure);
        }

        return Task.FromResult<Result<AuthUserSummaryDto>>(
            new AuthUserSummaryDto(UserId ?? Guid.Empty, IsProfileCompleted));
    }
}
