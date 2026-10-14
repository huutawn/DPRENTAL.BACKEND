using Dprental.InventoryService.Application.Options;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Meilisearch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dprental.InventoryService.Application.Search.Workers;

/// <summary>Host-owned, cancellable bootstrap and periodic index-loss detection.</summary>
public sealed class SearchBootstrapHostedService(IServiceScopeFactory scopes,
    IOptions<MeilisearchOptions> options, ILogger<SearchBootstrapHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
                var generation = await store.GetGenerationAsync(stoppingToken);
                if (generation?.ErrorCode == "SEARCH_SUBMISSION_UNKNOWN")
                    throw new InvalidOperationException("Unresolved live submission requires task reconciliation.");
                var provisioner = scope.ServiceProvider.GetRequiredService<IInventoryIndexProvisioner>();
                var active = await store.GetActiveBackfillRunAsync(stoppingToken);
                bool rebuild = generation?.IsReady != true || active is not null;
                if (active?.Status != SearchBackfillRun.StatusSwapping)
                {
                    await using (var writer = await store.TryAcquireCoordinationAsync(SearchCoordination.WriterKey, stoppingToken))
                    {
                        if (writer is null) { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); continue; }
                        if (!await provisioner.LiveIndexesExistAsync(stoppingToken))
                        {
                            generation ??= SearchGeneration.CreateInitial(options.Value.PublicListingsIndex,
                                options.Value.OwnerListingsIndex, options.Value.OwnerAssetsIndex, DateTimeOffset.UtcNow);
                            generation.MarkUnavailable(null, DateTimeOffset.UtcNow);
                            await store.SaveGenerationAsync(generation, stoppingToken);
                            rebuild = true;
                        }
                        await provisioner.EnsureIndexesAndSettingsAsync(stoppingToken);
                    }
                }
                if (rebuild)
                    await scope.ServiceProvider.GetRequiredService<IInventorySearchBackfillService>().BackfillAsync(stoppingToken);
                attempt = 0;
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (MeilisearchApiError ex) when (ex.Code is "invalid_api_key" or "missing_authorization_header" or "invalid_settings")
            {
                await RecordConfigurationErrorAsync(ex.Code is "invalid_api_key" or "missing_authorization_header"
                    ? "SEARCH_CREDENTIALS_INVALID" : "SEARCH_CONFIGURATION_INVALID", stoppingToken);
                logger.LogError("Search bootstrap configuration rejected: {Code}. Correct configuration and restart.", ex.Code);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Search bootstrap pending; retrying without blocking Inventory API.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++attempt, 6)))), stoppingToken);
            }
        }
    }

    private async Task RecordConfigurationErrorAsync(string error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISearchProjectionStore>();
        var generation = await store.GetGenerationAsync(ct) ?? SearchGeneration.CreateInitial(options.Value.PublicListingsIndex,
            options.Value.OwnerListingsIndex, options.Value.OwnerAssetsIndex, DateTimeOffset.UtcNow);
        generation.MarkUnavailable(error, DateTimeOffset.UtcNow);
        await store.SaveGenerationAsync(generation, ct);
    }
}
