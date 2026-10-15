using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository truy xuất và lưu trữ aggregate AssetAllocation trên PostgreSQL.
/// </summary>
public sealed class AssetAllocationRepository : IAssetAllocationRepository
{
    private readonly InventoryServiceDbContext _context;

    public AssetAllocationRepository(InventoryServiceDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<AssetAllocation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.AssetAllocations
            .Include(a => a.Transitions)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AssetAllocation?> GetByIdForOwnerAsync(Guid id, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        return await _context.AssetAllocations
            .Include(a => a.Transitions)
            .Include(a => a.Asset)
                .ThenInclude(ast => ast.Listing)
            .FirstOrDefaultAsync(a => a.Id == id && a.Asset.Listing.OwnerUserId == ownerUserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AssetAllocation?> GetByExternalReferenceAsync(string sourceSystem, string externalReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalReference);

        return await _context.AssetAllocations
            .Include(a => a.Transitions)
            .FirstOrDefaultAsync(a => a.SourceSystem == sourceSystem.Trim() && a.ExternalReference == externalReference.Trim(), cancellationToken);
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetAllocation>> GetByGroupReferenceAsync(
        string sourceSystem,
        string groupReference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceSystem) || string.IsNullOrWhiteSpace(groupReference))
        {
            return Array.Empty<AssetAllocation>();
        }

        string trimmedSource = sourceSystem.Trim();
        string trimmedGroup = groupReference.Trim();

        return await _context.AssetAllocations
            .Include(a => a.Transitions)
            .Include(a => a.Asset)
                .ThenInclude(ast => ast.Listing)
            .Where(a => a.SourceSystem == trimmedSource && a.GroupReference == trimmedGroup)
            .OrderBy(a => a.AssetId)
            .ToListAsync(cancellationToken);
    }


    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetAllocation>> GetActiveCandidatesAsync(
        Guid assetId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        return await _context.AssetAllocations
            .Include(a => a.Transitions)
            .Where(a => a.AssetId == assetId &&
                        (a.Status == AllocationStatus.Held || a.Status == AllocationStatus.Allocated) &&
                        a.RentalPeriod.StartDate <= endDate &&
                        (a.RentalPeriod.EndDate >= startDate || (a.Status == AllocationStatus.Allocated && a.HandedOverAt != null && a.ReturnedAt == null)))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetAllocation>> GetExpiredHoldsAsync(
        Guid assetId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await _context.AssetAllocations
            .Include(a => a.Transitions)
            .Where(a => a.AssetId == assetId &&
                        a.Status == AllocationStatus.Held &&
                        a.HoldExpiresAt != null &&
                        a.HoldExpiresAt <= now)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetAllocation>> ListByAssetAsync(
        Guid assetId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.AssetAllocations
            .AsNoTracking()
            .Where(a => a.AssetId == assetId)
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssetAllocation>> ListByOwnerAsync(
        Guid ownerUserId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.AssetAllocations
            .AsNoTracking()
            .Include(a => a.Asset)
                .ThenInclude(ast => ast.Listing)
            .Where(a => a.Asset.Listing.OwnerUserId == ownerUserId)
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(AssetAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _context.AssetAllocations.Add(allocation);
    }
    /// <inheritdoc />
    public void AddRange(IEnumerable<AssetAllocation> allocations)
    {
        ArgumentNullException.ThrowIfNull(allocations);
        _context.AssetAllocations.AddRange(allocations);
    }


    /// <inheritdoc />
    public void Update(AssetAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _context.AssetAllocations.Update(allocation);
    }
}
