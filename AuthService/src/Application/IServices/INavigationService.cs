using Dprental.AuthService.Common;
using Dprental.AuthService.Domain.DTOs.Navigation;

namespace Dprental.AuthService.Application.IServices;

/// <summary>Resolve navigation dành cho user hiện tại từ quyền trong database, không dựa trên permission claim của JWT.</summary>
public interface INavigationService
{
    Task<Result<NavigationResponse>> GetNavigationAsync(
        Guid userId,
        Guid? organizationId,
        CancellationToken cancellationToken = default);
}
