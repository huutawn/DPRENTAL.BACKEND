using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dprental.InventoryService.Domain.Repositories;

/// <summary>
/// Factory khởi tạo InventoryServiceDbContext tại thời điểm thiết kế (Design-time) phục vụ công cụ dotnet-ef migrations.
/// Giúp tạo migration độc lập mà không phụ thuộc vào trạng thái khởi động thực tế của Web Application.
/// </summary>
public sealed class InventoryServiceDbContextFactory : IDesignTimeDbContextFactory<InventoryServiceDbContext>
{
    /// <summary>
    /// Khởi tạo đối tượng InventoryServiceDbContext với cấu hình Npgsql mẫu phục vụ sinh mã migration.
    /// </summary>
    /// <param name="args">Các tham số dòng lệnh truyền vào từ dotnet-ef.</param>
    /// <returns>Đối tượng DbContext sẵn sàng cho công cụ thiết kế.</returns>
    public InventoryServiceDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<InventoryServiceDbContext>();

        // Chuỗi kết nối thiết kế mẫu phục vụ biên dịch metadata và mapping của PostgreSQL provider
        string designConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=inventory-design.invalid;Database=inventory_design;Username=design;Timeout=1";
        optionsBuilder.UseNpgsql(designConnectionString);

        return new InventoryServiceDbContext(optionsBuilder.Options);
    }
}
