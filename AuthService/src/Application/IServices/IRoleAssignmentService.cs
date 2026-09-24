using Dprental.AuthService.Common;
using Dprental.AuthService.Application.DTOs.Auth;

namespace Dprental.AuthService.Application.IServices;

public interface IRoleAssignmentService
{
    Task<Result> AssignAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default);

    Task<Result> RevokeAsync(Guid actorUserId, AssignRoleRequest request, CancellationToken cancellationToken = default);
}
