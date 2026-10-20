using System.Text.Json;
using System.Text.Json.Nodes;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dprental.RentalService.Maintenance;

public static class Program
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
        Console.WriteLine("  dotnet run --project RentalService/src/Maintenance -- replay-outbox --message-id <Guid>");
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
}
