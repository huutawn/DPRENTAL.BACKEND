using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.IServices.Reminders;
using Dprental.RentalService.Application.Services.Reminders;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.Maintenance;

public static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        bool apply = args.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase));

        var config = BuildConfiguration();
        string? connStr = config.GetConnectionString("RentalConnection")
                          ?? config["DATABASE_URL"]
                          ?? config["POSTGRES_CONNECTION_STRING"]
                          ?? "Host=localhost;Port=5433;Database=rentalservice;Username=postgres;Password=postgres";

        if (command == "reconcile-refund-confirmations")
        {
            string? syncConnection = config.GetConnectionString("Postgres");
            if (string.IsNullOrWhiteSpace(syncConnection)) syncConnection = connStr;
            try { return await RunRefundReconciliationAsync(config, syncConnection, apply); }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Refund reconciliation failed: {exception.GetType().Name}");
                return 1;
            }
        }

        var optionsBuilder = new DbContextOptionsBuilder<RentalServiceDbContext>();
        optionsBuilder.UseNpgsql(connStr);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddHttpClient<IInventoryRentalClient, HttpInventoryRentalClient>();
        var sp = services.BuildServiceProvider();
        var inventoryClient = sp.GetRequiredService<IInventoryRentalClient>();

        if (command == "backfill-equipment-types")
        {
            return await RunBackfillAsync(optionsBuilder.Options, inventoryClient, apply);
        }
        else if (command is "reconcile-return-requests" or "reconcile-returns")
        {
            try
            {
                int count = await RunReconcileReturnRequestsAsync(optionsBuilder.Options, apply);
                Console.WriteLine($"Reconciliation completed successfully: {count} record(s).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Reconciliation failed: {ex.Message}");
                return 1;
            }
        }
        else if (command == "replay-outbox")
        {
            string? idArg = null;
            for (int i = 1; i < args.Length; i++)
            {
                if ((args[i] == "--message-id" || args[i] == "-m") && i + 1 < args.Length)
                {
                    idArg = args[i + 1];
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(idArg) || !Guid.TryParse(idArg, out var messageId))
            {
                Console.Error.WriteLine("Error: --message-id <Guid> is required for replay-outbox.");
                return 1;
            }

            return await RunReplayAsync(optionsBuilder.Options, messageId);
        }
        else if (command is "backfill-reminders" or "backfill-reminder-schedules")
        {
            int batchSize = 100;
            string? phase = null;
            Guid? afterItemId = null;

            for (int i = 1; i < args.Length; i++)
            {
                if ((args[i] == "--batch-size" || args[i] == "-b") && i + 1 < args.Length && int.TryParse(args[i + 1], out int bs))
                {
                    batchSize = bs;
                    i++;
                }
                else if (args[i] == "--phase" && i + 1 < args.Length)
                {
                    phase = args[i + 1];
                    i++;
                }
                else if (args[i] == "--after-item-id" && i + 1 < args.Length && Guid.TryParse(args[i + 1], out var aid))
                {
                    afterItemId = aid;
                    i++;
                }
            }

            if (afterItemId.HasValue && string.IsNullOrWhiteSpace(phase))
            {
                Console.Error.WriteLine("Error: --phase <handover|return> is required when --after-item-id is provided.");
                return 1;
            }

            try
            {
                return await RunReminderBackfillAsync(optionsBuilder.Options, apply, batchSize, phase, afterItemId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Reminder backfill failed: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 1;
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("RentalService Maintenance CLI");
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project RentalService/src/Maintenance -- backfill-equipment-types [--apply]");
        Console.WriteLine("  dotnet run --project RentalService/src/Maintenance -- reconcile-return-requests [--apply]");
        Console.WriteLine("  dotnet run --project RentalService/src/Maintenance -- reconcile-refund-confirmations [--apply]");
        Console.WriteLine("  dotnet run --project RentalService/src/Maintenance -- replay-outbox --message-id <Guid>");
        Console.WriteLine("  dotnet run --project RentalService/src/Maintenance -- backfill-reminders [--apply] [--batch-size N] [--phase <handover|return>] [--after-item-id <guid>]");
        Console.WriteLine();
        Console.WriteLine("Options for backfill-reminders:");
        Console.WriteLine("  --apply              Persist reminder schedules to the database (default: dry-run).");
        Console.WriteLine("  --batch-size, -b     Candidate batch size per page (default: 100).");
        Console.WriteLine("  --phase              Starting phase ('handover' or 'return'). Handover automatically proceeds to return upon completion.");
        Console.WriteLine("  --after-item-id      Keyset cursor to resume after a specific item ID within the starting phase.");
    }

    public static async Task<int> RunBackfillAsync(
        DbContextOptions<RentalServiceDbContext> dbOptions,
        IInventoryRentalClient inventoryClient,
        bool apply)
    {
        Console.WriteLine($"=== Backfill Equipment Types (Mode: {(apply ? "APPLY" : "DRY-RUN")}) ===");

        int totalCartItemsChecked = 0;
        int totalCartItemsUpdated = 0;
        int totalRentalItemsChecked = 0;
        int totalRentalItemsUpdated = 0;
        int totalOpsChecked = 0;
        int totalOpsUpdated = 0;
        var unresolvedListings = new HashSet<Guid>();
        var listingTypeCache = new Dictionary<Guid, Guid>();

        // 1. Backfill Active Cart Items using keyset pagination
        Guid lastCartItemId = Guid.Empty;
        while (true)
        {
            await using var db = new RentalServiceDbContext(dbOptions);
            var items = await db.RentalCartItems
                .Include(i => i.RentalCart)
                .Where(i => i.Id > lastCartItemId &&
                            i.RentalCart != null &&
                            i.RentalCart.Status == RentalCartStatus.Active &&
                            (i.EquipmentTypeId == null || i.EquipmentTypeId == Guid.Empty))
                .OrderBy(i => i.Id)
                .Take(100)
                .ToListAsync();

            if (items.Count == 0) break;
            lastCartItemId = items[^1].Id;
            totalCartItemsChecked += items.Count;

            var neededListingIds = items.Select(i => i.EquipmentListingId).Distinct()
                .Where(id => !listingTypeCache.ContainsKey(id)).ToList();

            await ResolveListingsAsync(inventoryClient, neededListingIds, listingTypeCache, unresolvedListings);

            int batchUpdated = 0;
            foreach (var item in items)
            {
                if (listingTypeCache.TryGetValue(item.EquipmentListingId, out var typeId))
                {
                    item.SetEquipmentTypeId(typeId);
                    batchUpdated++;
                }
            }

            if (apply && batchUpdated > 0)
            {
                await db.SaveChangesAsync();
            }
            totalCartItemsUpdated += batchUpdated;
        }

        // 2. Backfill Ongoing Rental Items using keyset pagination
        Guid lastRentalItemId = Guid.Empty;
        while (true)
        {
            await using var db = new RentalServiceDbContext(dbOptions);
            var items = await db.RentalItems
                .Where(i => i.Id > lastRentalItemId &&
                            i.Status != RentalItemStatus.Completed &&
                            i.Status != RentalItemStatus.Rejected &&
                            i.Status != RentalItemStatus.Expired &&
                            i.Status != RentalItemStatus.Cancelled &&
                            (i.EquipmentTypeId == null || i.EquipmentTypeId == Guid.Empty))
                .OrderBy(i => i.Id)
                .Take(100)
                .ToListAsync();

            if (items.Count == 0) break;
            lastRentalItemId = items[^1].Id;
            totalRentalItemsChecked += items.Count;

            var neededListingIds = items.Select(i => i.EquipmentListingId).Distinct()
                .Where(id => !listingTypeCache.ContainsKey(id)).ToList();

            await ResolveListingsAsync(inventoryClient, neededListingIds, listingTypeCache, unresolvedListings);

            int batchUpdated = 0;
            foreach (var item in items)
            {
                if (listingTypeCache.TryGetValue(item.EquipmentListingId, out var typeId))
                {
                    item.SetEquipmentTypeId(typeId);
                    batchUpdated++;
                }
            }

            if (apply && batchUpdated > 0)
            {
                await db.SaveChangesAsync();
            }
            totalRentalItemsUpdated += batchUpdated;
        }

        // 3. Backfill Processing Checkout Operations using keyset pagination
        string lastOpId = string.Empty;
        while (true)
        {
            await using var db = new RentalServiceDbContext(dbOptions);
            var ops = await db.RentalCheckoutOperations
                .Where(o => string.Compare(o.OperationId, lastOpId, StringComparison.Ordinal) > 0 &&
                            o.Status == CheckoutOperationStatus.Processing)
                .OrderBy(o => o.OperationId)
                .Take(100)
                .ToListAsync();

            if (ops.Count == 0) break;
            lastOpId = ops[^1].OperationId;
            totalOpsChecked += ops.Count;

            int batchUpdated = 0;
            foreach (var op in ops)
            {
                if (string.IsNullOrWhiteSpace(op.PreparationSnapshotJson)) continue;

                try
                {
                    var node = JsonNode.Parse(op.PreparationSnapshotJson);
                    if (node?["lines"] is JsonArray lines)
                    {
                        bool modified = false;
                        var opNeededListingIds = new List<Guid>();

                        foreach (var lineNode in lines)
                        {
                            if (lineNode?["equipmentListingId"]?.GetValue<string>() is string lIdStr &&
                                Guid.TryParse(lIdStr, out var lId))
                            {
                                var snap = lineNode["listingSnapshot"];
                                if (snap?["equipmentTypeId"] == null && !listingTypeCache.ContainsKey(lId))
                                {
                                    opNeededListingIds.Add(lId);
                                }
                            }
                        }

                        if (opNeededListingIds.Count > 0)
                        {
                            await ResolveListingsAsync(inventoryClient, opNeededListingIds, listingTypeCache, unresolvedListings);
                        }

                        foreach (var lineNode in lines)
                        {
                            if (lineNode?["equipmentListingId"]?.GetValue<string>() is string lIdStr &&
                                Guid.TryParse(lIdStr, out var lId))
                            {
                                var snap = lineNode["listingSnapshot"];
                                if (snap?["equipmentTypeId"] == null && listingTypeCache.TryGetValue(lId, out var typeId))
                                {
                                    if (snap == null)
                                    {
                                        snap = new JsonObject();
                                        lineNode["listingSnapshot"] = snap;
                                    }
                                    snap["equipmentTypeId"] = typeId.ToString("D");
                                    modified = true;
                                }
                            }
                        }

                        if (modified)
                        {
                            op.UpdatePreparationSnapshot(node.ToJsonString(), DateTimeOffset.UtcNow);
                            batchUpdated++;
                        }
                    }
                }
                catch
                {
                    // Ignore corrupted JSON in non-critical operation
                }
            }

            if (apply && batchUpdated > 0)
            {
                await db.SaveChangesAsync();
            }
            totalOpsUpdated += batchUpdated;
        }

        Console.WriteLine($"Summary:");
        Console.WriteLine($"  - Active CartItems: {totalCartItemsUpdated}/{totalCartItemsChecked} updated");
        Console.WriteLine($"  - Ongoing RentalItems: {totalRentalItemsUpdated}/{totalRentalItemsChecked} updated");
        Console.WriteLine($"  - Processing Operations: {totalOpsUpdated}/{totalOpsChecked} updated");
        Console.WriteLine($"  - Unresolved Listings: {unresolvedListings.Count}");
        foreach (var u in unresolvedListings)
        {
            Console.WriteLine($"      ListingId {u} could not be resolved from Inventory.");
        }

        if (!apply)
        {
            Console.WriteLine("\n[DRY-RUN] No changes were persisted. Run with --apply to commit updates.");
        }
        else
        {
            Console.WriteLine("\n[APPLIED] Successfully backfilled snapshot data.");
        }

        return unresolvedListings.Count > 0 ? 2 : 0;
    }

    private static async Task ResolveListingsAsync(
        IInventoryRentalClient inventoryClient,
        List<Guid> listingIds,
        Dictionary<Guid, Guid> cache,
        HashSet<Guid> unresolved)
    {
        if (listingIds.Count == 0) return;

        // Batch in chunks of 100
        for (int i = 0; i < listingIds.Count; i += 100)
        {
            var chunk = listingIds.Skip(i).Take(100).ToList();
            var response = await inventoryClient.GetListingRentalSnapshotsAsync(
                new ListingRentalSnapshotsRequest(chunk));

            if (response.IsSuccess && response.Value.Items != null)
            {
                foreach (var item in response.Value.Items)
                {
                    if (item.EquipmentTypeId != Guid.Empty)
                    {
                        cache[item.Id] = item.EquipmentTypeId;
                    }
                    else
                    {
                        unresolved.Add(item.Id);
                    }
                }

                if (response.Value.MissingIds != null)
                {
                    foreach (var m in response.Value.MissingIds)
                    {
                        unresolved.Add(m);
                    }
                }
            }
            else
            {
                foreach (var id in chunk)
                {
                    unresolved.Add(id);
                }
            }
        }
    }

    public static async Task<int> RunReplayAsync(
        DbContextOptions<RentalServiceDbContext> dbOptions,
        Guid messageId)
    {
        Console.WriteLine($"=== Replaying Outbox Message: {messageId:D} ===");

        await using var db = new RentalServiceDbContext(dbOptions);
        var msg = await db.RentalOutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId);

        if (msg == null)
        {
            Console.Error.WriteLine($"Error: Outbox message '{messageId:D}' not found.");
            return 1;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (msg.LockedUntilUtc.HasValue && msg.LockedUntilUtc.Value > nowUtc)
        {
            Console.Error.WriteLine($"Error: Cannot replay message '{messageId:D}' - active lease held until {msg.LockedUntilUtc.Value:O}.");
            return 1;
        }

        msg.ResetForReplay(nowUtc);
        await db.SaveChangesAsync();

        Console.WriteLine($"Message '{messageId:D}' successfully reset to 'Pending' for replay at {nowUtc:O}.");
        return 0;
    }
    public static async Task<int> RunReconcileReturnRequestsAsync(
        DbContextOptions<RentalServiceDbContext> dbOptions,
        bool apply,
        int batchSize = 100,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await output.WriteLineAsync($"=== Reconcile Return Requests (Mode: {(apply ? "APPLY" : "DRY-RUN")}) ===");

        if (batchSize <= 0) batchSize = 100;

        await using var countDb = new RentalServiceDbContext(dbOptions);
        var eligibleCount = await (from u in countDb.ReturnRequestUnits
                                   join r in countDb.ReturnRequests on u.ReturnRequestId equals r.Id
                                   join a in countDb.RentalUnitAssignments on u.RentalUnitAssignmentId equals a.Id
                                   join ret in countDb.ReturnRecords on a.ReturnRecordId equals ret.Id
                                   where u.ClosedAt == null && u.RejectedAt == null
                                         && a.RentalItemId == r.RentalItemId
                                         && ret.RentalItemId == r.RentalItemId
                                   select u.Id).CountAsync();

        await output.WriteLineAsync($"Found {eligibleCount} unclosed return request unit(s) with completed return record(s).");

        if (!apply)
        {
            await output.WriteLineAsync("Dry-run complete. No changes were made.");
            return eligibleCount;
        }

        int totalUpdated = 0;
        while (true)
        {
            await using var db = new RentalServiceDbContext(dbOptions);
            var batch = await (from u in db.ReturnRequestUnits
                               join r in db.ReturnRequests on u.ReturnRequestId equals r.Id
                               join a in db.RentalUnitAssignments on u.RentalUnitAssignmentId equals a.Id
                               join ret in db.ReturnRecords on a.ReturnRecordId equals ret.Id
                               where u.ClosedAt == null && u.RejectedAt == null
                                     && a.RentalItemId == r.RentalItemId
                                     && ret.RentalItemId == r.RentalItemId
                               orderby u.Id
                               select new
                               {
                                   Unit = u,
                                   ClosedAt = ret.SubmittedAt
                               })
                               .Take(batchSize)
                               .ToListAsync();

            if (batch.Count == 0)
            {
                break;
            }

            await using var tx = await db.Database.BeginTransactionAsync();
            foreach (var item in batch)
            {
                item.Unit.SetClosedAt(item.ClosedAt);
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            totalUpdated += batch.Count;
            await output.WriteLineAsync($"Processed batch: reconciled {batch.Count} unit(s). Total so far: {totalUpdated}");
        }

        await output.WriteLineAsync($"Reconciliation complete. Total updated: {totalUpdated} unit(s).");
        return totalUpdated;
    }

    public static async Task<int> RunReminderBackfillAsync(
        DbContextOptions<RentalServiceDbContext> dbOptions,
        bool apply,
        int batchSize = 100,
        string? phase = null,
        Guid? afterItemId = null,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        await output.WriteLineAsync($"=== Backfill Reminder Schedules (Mode: {(apply ? "APPLY" : "DRY-RUN")}, BatchSize: {batchSize}, Phase: {phase ?? "all"}, Checkpoint: {afterItemId?.ToString() ?? "None"}) ===");

        if (batchSize <= 0) batchSize = 100;

        if (afterItemId.HasValue && string.IsNullOrWhiteSpace(phase))
        {
            await output.WriteLineAsync("Error: Phase is required when afterItemId is provided.");
            return 1;
        }

        var services = new ServiceCollection();
        services.AddSingleton(dbOptions);
        services.AddScoped(sp => new RentalServiceDbContext(dbOptions));
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        services.AddScoped<IRentalReminderScheduleRepository, RentalReminderScheduleRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        await using (var testDb = new RentalServiceDbContext(dbOptions))
        {
            if (testDb.Database.IsNpgsql())
            {
                string connString = testDb.Database.GetConnectionString() ?? "";
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connString
                }).Build();
                services.AddSingleton<IConfiguration>(config);
                services.AddSingleton<IOrderLockManager, PostgresOrderLockManager>();
            }
            else
            {
                services.AddSingleton<IOrderLockManager, NoOpOrderLockManager>();
            }
        }

        services.AddScoped<IRentalReminderBackfillService>(sp =>
            new RentalReminderBackfillService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IOrderLockManager>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RentalReminderBackfillService>>()));
        await using var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var backfillService = scope.ServiceProvider.GetRequiredService<IRentalReminderBackfillService>();

        var result = await backfillService.BackfillActiveItemsAsync(
            batchSize: batchSize,
            phase: phase,
            afterItemId: afterItemId,
            apply: apply,
            onBatchCompleted: progress =>
            {
                if (apply)
                {
                    output.WriteLine($"Batch {progress.BatchNumber}: Handover created: {progress.HandoverCreated}, Return created: {progress.ReturnCreated}, Phase: {progress.Phase}, Checkpoint: {progress.LastProcessedItemId}");
                    output.WriteLine($"  Resume command: {progress.ResumeCommand}");
                }
                else
                {
                    output.WriteLine($"[DRY-RUN] Batch {progress.BatchNumber} [{progress.Phase}]: Handover candidates: {progress.HandoverCreated}, Return candidates: {progress.ReturnCreated}, Cursor: {progress.LastProcessedItemId}");
                }
            },
            cancellationToken: cancellationToken);

        if (!result.Succeeded)
        {
            string resumeSuggestion = result.LastProcessedItemId.HasValue
                ? $" Resume command: dotnet run --project RentalService/src/Maintenance -- backfill-reminders {(apply ? "--apply " : "")}--batch-size {batchSize} --phase {result.LastPhase} --after-item-id {result.LastProcessedItemId.Value}"
                : $" Resume command: dotnet run --project RentalService/src/Maintenance -- backfill-reminders {(apply ? "--apply " : "")}--batch-size {batchSize} --phase {result.LastPhase}";
            await output.WriteLineAsync($"Backfill failed: {result.ErrorMessage}. Previous checkpoint: {result.LastProcessedItemId?.ToString() ?? "None"}.{resumeSuggestion}");
            return 1;
        }

        if (apply)
        {
            await output.WriteLineAsync($"Backfill completed: {result.HandoverSchedulesCreated} Handover schedule(s), {result.ReturnSchedulesCreated} Return schedule(s) created across {result.TotalBatches} batch(es). Final Checkpoint: {result.LastProcessedItemId?.ToString() ?? "None"}.");
        }
        else
        {
            await output.WriteLineAsync($"Backfill completed (DRY-RUN): {result.HandoverSchedulesCreated} Handover schedule(s), {result.ReturnSchedulesCreated} Return schedule(s) would be created across {result.TotalBatches} batch(es). Final Cursor: {result.LastProcessedItemId?.ToString() ?? "None"}.");
        }

        return 0;
    }

    private sealed class NoOpOrderLockManager : IOrderLockManager
    {
        private sealed class Releaser : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private static readonly IAsyncDisposable SharedReleaser = new Releaser();

        public Task<IAsyncDisposable> AcquireOrderLockAsync(Guid orderId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(SharedReleaser);

        public Task<IAsyncDisposable> AcquireRenterLockAsync(Guid renterUserId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            => Task.FromResult(SharedReleaser);
    }
}
