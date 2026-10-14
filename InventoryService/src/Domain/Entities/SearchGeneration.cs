using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Dprental.InventoryService.Domain.Entities;

/// <summary>
/// Thực thể lưu trữ trạng thái bền vững của generation chỉ mục live và readiness của InventoryService.
/// </summary>
[Table("search_generations", Schema = "inventory")]
public sealed class SearchGeneration
{
    public const int DefaultSingletonId = 1;

    [Key]
    [Column("id")]
    public int Id { get; private set; } = DefaultSingletonId;

    [Required]
    [MaxLength(100)]
    [Column("live_public_index")]
    public string LivePublicIndex { get; private set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("live_owner_listings_index")]
    public string LiveOwnerListingsIndex { get; private set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("live_owner_assets_index")]
    public string LiveOwnerAssetsIndex { get; private set; } = string.Empty;

    [Column("is_ready")]
    public bool IsReady { get; private set; }

    [Column("active_run_id")]
    public Guid? ActiveRunId { get; private set; }

    [Column("error_code")]
    public string? ErrorCode { get; private set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; private set; }

    private SearchGeneration() { }

    public static SearchGeneration CreateInitial(
        string publicIndex,
        string ownerListingsIndex,
        string ownerAssetsIndex,
        DateTimeOffset now)
    {
        return new SearchGeneration
        {
            Id = DefaultSingletonId,
            LivePublicIndex = publicIndex,
            LiveOwnerListingsIndex = ownerListingsIndex,
            LiveOwnerAssetsIndex = ownerAssetsIndex,
            IsReady = false,
            ActiveRunId = null,
            UpdatedAt = now
        };
    }

    public void MarkReady(DateTimeOffset now)
    {
        IsReady = true;
        ErrorCode = null;
        UpdatedAt = now;
    }

    public void MarkUnavailable(string? errorCode, DateTimeOffset now)
    {
        IsReady = false;
        ErrorCode = errorCode;
        UpdatedAt = now;
    }

    public void SetActiveRun(Guid? runId, DateTimeOffset now)
    {
        ActiveRunId = runId;
        UpdatedAt = now;
    }
}
