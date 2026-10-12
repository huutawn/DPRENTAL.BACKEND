using System.Globalization;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.IRepositories;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RentalService.Domain.Repositories;

/// <summary>
/// Lấy số kế tiếp từ sequence PostgreSQL nên hai lần checkout đồng thời không bao giờ trùng mã.
/// Số đã lấy không được trả lại khi checkout thất bại, nên mã đơn có thể nhảy số.
/// </summary>
public sealed class PostgresOrderCodeGenerator : IOrderCodeGenerator
{
    // Năm trong mã đơn tính theo giờ Việt Nam (UTC+7, không có DST).
    private static readonly TimeSpan BusinessUtcOffset = TimeSpan.FromHours(7);

    private readonly RentalServiceDbContext _dbContext;

    public PostgresOrderCodeGenerator(RentalServiceDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<string> NextAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        List<long> values = await _dbContext.Database
            .SqlQuery<long>($"SELECT nextval('rental.order_code_seq') AS \"Value\"")
            .ToListAsync(cancellationToken);

        int year = utcNow.ToOffset(BusinessUtcOffset).Year;
        return string.Create(CultureInfo.InvariantCulture, $"DH-{year}-{values[0]:D6}");
    }
}
