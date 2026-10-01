using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Triển khai repository quản lý báo cáo hư hỏng thiết bị (DamageReport) bằng EF Core.
/// </summary>
public sealed class DamageReportRepository : IDamageReportRepository
{
    private readonly InventoryServiceDbContext _context;

    /// <summary>
    /// Khởi tạo repository với DbContext được inject.
    /// </summary>
    /// <param name="context">DbContext của Inventory Service.</param>
    public DamageReportRepository(InventoryServiceDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<DamageReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.DamageReports
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DamageReport>> ListByReservationAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        return await _context.DamageReports
            .Where(d => d.ReservationId == reservationId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DamageReport>> ListByEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken = default)
    {
        return await _context.DamageReports
            .Where(d => d.Reservation.EquipmentId == equipmentId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DamageReport>> ListByReporterAsync(
        Guid reporterAccountId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await _context.DamageReports
            .AsNoTracking()
            .Where(d => d.ReporterAccountId == reporterAccountId)
            .OrderByDescending(d => d.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Add(DamageReport report)
    {
        _context.DamageReports.Add(report);
    }

    /// <inheritdoc />
    public void Update(DamageReport report)
    {
        var entry = _context.Entry(report);
        if (entry.State == EntityState.Detached)
        {
            _context.DamageReports.Update(report);
        }
    }
}
