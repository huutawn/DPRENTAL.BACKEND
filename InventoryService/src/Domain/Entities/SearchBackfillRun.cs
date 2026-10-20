using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể ghi nhận tiến trình từng đợt backfill và atomic swap index của InventoryService.
/// </summary>
[Table("search_backfill_runs", Schema = "inventory")]
public sealed class SearchBackfillRun
{
    public const string StatusBuilding = "Building";
    public const string StatusCatchingUp = "CatchingUp";
    public const string StatusSwapping = "Swapping";
    public const string StatusCompleted = "Completed";
    public const string StatusFailed = "Failed";

    [Key]
    [Column("run_id")]
    public Guid RunId { get; private set; }

    [Required]
    [MaxLength(30)]
    [Column("status")]
    public string Status { get; private set; } = StatusBuilding;

    [Required]
    [MaxLength(100)]
    [Column("staging_public_index")]
    public string StagingPublicIndex { get; private set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("staging_owner_listings_index")]
    public string StagingOwnerListingsIndex { get; private set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("staging_owner_assets_index")]
    public string StagingOwnerAssetsIndex { get; private set; } = string.Empty;

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; private set; }

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; private set; }

    [Column("starting_outbox_sequence")]
    public long? StartingOutboxSequence { get; private set; }

    [Column("swap_task_uid")]
    public int? SwapTaskUid { get; private set; }

    [Column("listing_cursor")]
    public Guid? ListingCursor { get; private set; }

    [Column("asset_cursor")]
    public Guid? AssetCursor { get; private set; }

    [Column("listings_loaded")]
    public bool ListingsLoaded { get; private set; }

    [Column("assets_loaded")]
    public bool AssetsLoaded { get; private set; }

    [Column("error_message")]
    public string? ErrorMessage { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    public void AdvanceListings(Guid? cursor, bool completed, DateTimeOffset now)
    { ListingCursor = cursor; ListingsLoaded = completed; UpdatedAt = now; }

    public void AdvanceAssets(Guid? cursor, bool completed, DateTimeOffset now)
    { AssetCursor = cursor; AssetsLoaded = completed; UpdatedAt = now; }

    public void Transition(string status, DateTimeOffset now, string? error = null)
    {
        Status = status; UpdatedAt = now; ErrorMessage = error;
        if (status is StatusCompleted or StatusFailed) CompletedAt = now;
    }

    public void RecordSwap(int uid, DateTimeOffset now)
    { SwapTaskUid = uid; UpdatedAt = now; }

    private SearchBackfillRun() { }

    public static SearchBackfillRun Create(
        Guid runId,
        string stagingPublicIndex,
        string stagingOwnerListingsIndex,
        string stagingOwnerAssetsIndex,
        long? startingOutboxSequence,
        DateTimeOffset now)
    {
        return new SearchBackfillRun
        {
            RunId = runId,
            Status = StatusBuilding,
            StagingPublicIndex = stagingPublicIndex,
            StagingOwnerListingsIndex = stagingOwnerListingsIndex,
            StagingOwnerAssetsIndex = stagingOwnerAssetsIndex,
            StartedAt = now,
            StartingOutboxSequence = startingOutboxSequence,
            UpdatedAt = now
        };
    }
}
