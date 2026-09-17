using DpRent.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Infrastructure.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        // Tên bảng và Schema
        builder.ToTable("Users", "auth");

        // Khóa chính
        builder.HasKey(x => x.Id);

        // Các ràng buộc (Data Annotations cũ chuyển hết vào đây)
        builder.HasIndex(x => x.Email).IsUnique();
        builder.Property(x => x.FullName).IsRequired().HasMaxLength(100);
    }
}