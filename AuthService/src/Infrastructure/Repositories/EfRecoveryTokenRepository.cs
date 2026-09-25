using Dprental.AuthService.Application.Repositories;
using Dprental.AuthService.Domain.Entities;
using Dprental.AuthService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Dprental.AuthService.Infrastructure.Repositories;

public class EfRecoveryTokenRepository(AuthDbContext dbContext) : IRecoveryTokenRepository
{
    public async Task AddAsync(RecoveryToken token, CancellationToken cancellationToken = default)
    {
        await dbContext.RecoveryTokens.AddAsync(token, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<RecoveryToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        return await dbContext.RecoveryTokens
            .FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);
    }

    public async Task UpdateAsync(RecoveryToken token, CancellationToken cancellationToken = default)
    {
        dbContext.RecoveryTokens.Update(token);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
