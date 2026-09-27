using Dprental.MediaService.Common.Enums;
using Dprental.MediaService.Domain.Entities;
using Dprental.MediaService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.MediaService.Domain.Repositories;

public class MediaUploadSessionRepository : IMediaUploadSessionRepository
{
    private readonly MediaDbContext _context;

    public MediaUploadSessionRepository(MediaDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<MediaUploadSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.MediaUploadSessions
            .Include(s => s.MediaAsset)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<MediaUploadSession?> GetByIdUntrackedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.MediaUploadSessions.AsNoTracking()
            .Include(s => s.MediaAsset)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<MediaUploadSession?> FindByCallerAndIdempotencyKeyAsync(
        string callerIdentity,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await _context.MediaUploadSessions
            .Include(s => s.MediaAsset)
            .FirstOrDefaultAsync(
                s => s.CallerIdentity == callerIdentity && s.IdempotencyKey == idempotencyKey,
                cancellationToken);
    }

    public async Task AddAsync(MediaUploadSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _context.MediaUploadSessions.AddAsync(session, cancellationToken);
    }

    public Task UpdateAsync(MediaUploadSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Version++;
        _context.MediaUploadSessions.Update(session);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MediaUploadSession>> GetExpiredPendingSessionsAsync(
        DateTimeOffset cutoffUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await _context.MediaUploadSessions
            .Where(s => s.Status == UploadSessionStatus.Pending && s.ExpiresAtUtc <= cutoffUtc)
            .OrderBy(s => s.ExpiresAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
