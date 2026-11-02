using Dprental.NotificationService.Domain.Entities;
using Dprental.NotificationService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.NotificationService.Domain.Repositories;

public sealed class PreferenceRepository : IPreferenceRepository
{
    private readonly NotificationServiceDbContext _dbContext;

    public PreferenceRepository(NotificationServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<IReadOnlyList<NotificationPreference>> GetUserPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Preferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> IsEmailEnabledAsync(Guid userId, string category, CancellationToken cancellationToken = default)
    {
        var pref = await _dbContext.Preferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.Category == category, cancellationToken);

        // Safe default: true when not explicitly disabled
        return pref?.EmailEnabled ?? true;
    }

    public async Task UpsertPreferencesAsync(
        Guid userId,
        IEnumerable<(string Category, bool EmailEnabled)> preferences,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _dbContext.Preferences
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);

        foreach (var (category, emailEnabled) in preferences)
        {
            var match = existing.FirstOrDefault(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                match.EmailEnabled = emailEnabled;
                match.UpdatedAt = now;
            }
            else
            {
                await _dbContext.Preferences.AddAsync(new NotificationPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Category = category.ToUpperInvariant(),
                    EmailEnabled = emailEnabled,
                    CreatedAt = now,
                    UpdatedAt = now
                }, cancellationToken);
            }
        }
    }
}
