using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository quản lý lượt đặt lịch thuê thiết bị (Reservation) và lịch sử transitions bằng EF Core.
/// </summary>
public sealed class ReservationRepository : IReservationRepository
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo repository với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public ReservationRepository(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .Include(r => r.Transitions.OrderBy(t => t.Sequence))
            .Include(r => r.DamageReports)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Reservation?> GetByIdForOrganizationAsync(
        Guid id,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .Include(r => r.Transitions.OrderBy(t => t.Sequence))
            .Include(r => r.DamageReports)
            .FirstOrDefaultAsync(r => r.Id == id && r.Equipment.OrganizationId == organizationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Reservation?> GetByExternalReferenceAsync(
        string sourceSystem,
        string externalReference,
        CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .Include(r => r.Transitions.OrderBy(t => t.Sequence))
            .FirstOrDefaultAsync(r => r.SourceSystem == sourceSystem && r.ExternalReference == externalReference, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Reservation>> GetActiveOrCompletedCandidatesAsync(
        Guid equipmentId,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var startUtc = Dprental.InventoryService.Common.Utils.BusinessDateTime.StartOfDayUtc(startDate);
        return await _context.Reservations
            .Where(r => r.EquipmentId == equipmentId &&
                (r.Status == ReservationStatus.InUse ||
                 r.Status == ReservationStatus.Completed && r.ReturnedAt >= startUtc ||
                 (r.Status == ReservationStatus.Held || r.Status == ReservationStatus.Confirmed) &&
                 r.RentalPeriod.StartDate <= endDate && r.RentalPeriod.EndDate >= startDate))
            .OrderBy(r => r.RentalPeriod.StartDate)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Reservation>> GetExpiredHoldsAsync(
        Guid equipmentId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .Include(r => r.Transitions.OrderBy(t => t.Sequence))
            .Where(r => r.EquipmentId == equipmentId &&
                r.Status == ReservationStatus.Held &&
                r.HoldExpiresAt != null &&
                r.HoldExpiresAt.Value <= now)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Reservation>> ListByRenterAsync(
        Guid renterAccountId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .AsNoTracking()
            .Where(r => r.RenterAccountId == renterAccountId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Reservation>> ListByOrganizationAsync(
        Guid organizationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .AsNoTracking()
            .Where(r => r.Equipment.OrganizationId == organizationId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasActiveRentalsForRenterAsync(Guid renterAccountId, CancellationToken cancellationToken = default)
    {
        return await _context.Reservations
            .AnyAsync(r => r.RenterAccountId == renterAccountId && r.Status == ReservationStatus.InUse, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetDamageReportsCountForReservationAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        return await _context.DamageReports
            .CountAsync(d => d.ReservationId == reservationId, cancellationToken);
    }

    /// <inheritdoc />
    public void Add(Reservation reservation)
    {
        _context.Reservations.Add(reservation);
    }

    /// <inheritdoc />
    public void Update(Reservation reservation)
    {
        var entry = _context.Entry(reservation);
        if (entry.State == EntityState.Detached)
        {
            _context.Reservations.Update(reservation);
        }
    }
}
