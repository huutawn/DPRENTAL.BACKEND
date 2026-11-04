using Dprental.InventoryService.Application.Clients;
using Dprental.InventoryService.Application.Kafka.Outbox;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dprental.InventoryService.Application.Services;

public sealed class ListingOrganizationBackfillWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration,
    ILogger<ListingOrganizationBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(configuration["InventorySupplierLookup:ApiKey"]))
        {
            logger.LogWarning("Listing organization backfill disabled: lookup key is not configured.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunPassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Listing organization backfill failed; it will retry."); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        Guid? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var candidates = await scope.ServiceProvider.GetRequiredService<IListingOrganizationBackfillRepository>()
                .GetMissingAsync(cursor, 100, cancellationToken);
            if (candidates.Count == 0) return;
            var lookup = scope.ServiceProvider.GetRequiredService<ISupplierOrganizationLookup>();
            var transaction = scope.ServiceProvider.GetRequiredService<IInventoryWriteTransaction>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var org = await lookup.FindByOwnerAsync(candidate.OwnerUserId, cancellationToken);
                if (org.HasValue)
                {
                    await transaction.ExecuteListingWriteAsync(candidate.ListingId, async (context, ct) =>
                    {
                        var listing = await context.Listings.GetByIdForOwnerAsync(candidate.ListingId, candidate.OwnerUserId, ct);
                        if (listing is null || listing.OrganizationId.HasValue) return Result.Success(false);
                        var now = clock.GetUtcNow();
                        listing.AssignOrganization(org.Value, now);
                        context.Listings.Update(listing);
                        var revision = await context.GetNextSearchRevisionAsync(ct);
                        context.StageOutbox(SearchProjectionEvents.CreateListingProjectionOutboxMessage(listing.Id, "Upsert", revision, false, now, context.SearchBackfillRunId));
                        context.StageCacheInvalidation([$"listing:{listing.Id:D}", $"owner:{candidate.OwnerUserId:D}", "public-discovery"]);
                        return Result.Success(true);
                    }, cancellationToken);
                }
                cursor = candidate.ListingId;
            }
        }
    }
}
