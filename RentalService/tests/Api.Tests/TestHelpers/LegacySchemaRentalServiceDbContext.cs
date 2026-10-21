using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Dprental.RentalService.Api.Tests.TestHelpers;

/// <summary>
/// Chỉ dùng để ghi dữ liệu mẫu trong test migration, khi database đang dừng ở một migration cũ.
/// Model hiện tại có những cột mà migration sau mới thêm; ghi bằng <see cref="RentalServiceDbContext"/> sẽ lỗi
/// "column does not exist". Context này bỏ các cột đó đi. Không dùng nó để chạy migration
/// (migration gắn với <see cref="RentalServiceDbContext"/>, không gắn với lớp con).
/// </summary>
/// <remarks>
/// Thêm cột mới vào một bảng mà test migration ghi dữ liệu mẫu (đơn, dòng thuê, receipt, inbox, giỏ) thì thêm Ignore ở đây.
/// </remarks>
internal sealed class LegacySchemaRentalServiceDbContext(DbContextOptions<RentalServiceDbContext> options)
    : RentalServiceDbContext(options)
{
    // Context này chỉ ghi dữ liệu mẫu nên không cần index. Bỏ quy ước đọc [Index] để Ignore được cột mới
    // dù cột đó nằm trong một [Index] (EF không cho index dùng cột đã Ignore).
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Remove<IndexAttributeConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 20261019085752_AddRentalCartItemEquipmentTypeId
        modelBuilder.Entity<RentalCartItem>().Ignore(item => item.EquipmentTypeId);

        // 20261020084157_AddInventoryOperationKeyToRentalReceipt
        modelBuilder.Entity<RentalOperationReceipt>().Ignore(receipt => receipt.InventoryOperationKey);

        // 20261021010246_AddPaymentPublicationProofToRentalInbox
        modelBuilder.Entity<RentalInboxMessage>().Ignore(message => message.KafkaMessageKey);
        modelBuilder.Entity<RentalInboxMessage>().Ignore(message => message.PublicationProofJson);
    }
}
