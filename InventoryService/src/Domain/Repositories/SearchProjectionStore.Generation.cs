using Dprental.InventoryService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

public sealed partial class SearchProjectionStore
{
    public Task<SearchGeneration?> GetGenerationAsync(CancellationToken cancellationToken = default) =>
        context.SearchGenerations.AsNoTracking().FirstOrDefaultAsync(g => g.Id == SearchGeneration.DefaultSingletonId, cancellationToken);

    public async Task SaveGenerationAsync(SearchGeneration generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var existing = await context.SearchGenerations.FirstOrDefaultAsync(g => g.Id == generation.Id, cancellationToken);
        if (existing is null) context.SearchGenerations.Add(generation);
        else context.Entry(existing).CurrentValues.SetValues(generation);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task<SearchBackfillRun?> GetActiveBackfillRunAsync(CancellationToken cancellationToken = default) =>
        context.SearchBackfillRuns.AsNoTracking().Where(r => r.Status == SearchBackfillRun.StatusBuilding ||
            r.Status == SearchBackfillRun.StatusCatchingUp || r.Status == SearchBackfillRun.StatusSwapping)
            .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task SaveBackfillRunAsync(SearchBackfillRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var existing = await context.SearchBackfillRuns.FirstOrDefaultAsync(r => r.RunId == run.RunId, cancellationToken);
        if (existing is null) context.SearchBackfillRuns.Add(run);
        else context.Entry(existing).CurrentValues.SetValues(run);
        await context.SaveChangesAsync(cancellationToken);
    }
}
