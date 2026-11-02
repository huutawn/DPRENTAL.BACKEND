using Dprental.NotificationService.Domain.Entities;

namespace Dprental.NotificationService.Domain.IRepositories;

public interface IPreferenceRepository
{
    Task<IReadOnlyList<NotificationPreference>> GetUserPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> IsEmailEnabledAsync(Guid userId, string category, CancellationToken cancellationToken = default);
    Task UpsertPreferencesAsync(Guid userId, IEnumerable<(string Category, bool EmailEnabled)> preferences, CancellationToken cancellationToken = default);
}
