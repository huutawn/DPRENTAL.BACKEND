using Dprental.AuthService.Common.Repositories;
using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IUserRepository : IRepo<User, Guid>
{
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken = default);

    Task<bool> TryCreateAsync(User user, CancellationToken cancellationToken = default);
}
