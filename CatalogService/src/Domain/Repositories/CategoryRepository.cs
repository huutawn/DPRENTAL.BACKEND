using Dprental.CatalogService.Domain.Entities;
using Dprental.CatalogService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.CatalogService.Domain.Repositories;

public class CategoryRepository : ICategoryRepository
{
    private readonly CatalogServiceDbContext _dbContext;

    public CategoryRepository(CatalogServiceDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Chú ý: Không dùng AsNoTracking() ở đây vì ta cần update entity này
        return await _dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<bool> IsSlugExistsAsync(string slug, CancellationToken cancellationToken = default)
    {
        return await _dbContext.Categories.AnyAsync(c => c.Slug == slug, cancellationToken);
    }

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        await _dbContext.Categories.AddAsync(category, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        _dbContext.Categories.Update(category);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Categories
            .Where(c => c.IsActive) // Chỉ lấy danh mục đang hoạt động
            .AsNoTracking()         // Tối ưu hiệu suất cho truy vấn đọc (Read-only)
            .ToListAsync(cancellationToken);
    }
}