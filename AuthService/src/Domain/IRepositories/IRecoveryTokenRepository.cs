using Dprental.AuthService.Domain.Entities;

namespace Dprental.AuthService.Domain.IRepositories;

public interface IRecoveryTokenRepository
{
    Task AddAsync(RecoveryToken token, CancellationToken cancellationToken = default);
    Task<RecoveryToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);
    Task UpdateAsync(RecoveryToken token, CancellationToken cancellationToken = default);
}