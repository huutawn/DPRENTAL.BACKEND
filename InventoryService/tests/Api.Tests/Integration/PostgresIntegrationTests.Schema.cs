using System.Data;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Policies;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresIntegrationTests
{
    /// <summary>
    /// Kiểm thử 1: Migration chạy thành công trên cơ sở dữ liệu PostgreSQL rỗng,
    /// tạo đúng schema inventory, sequence, 9 bảng và các metadata liên quan.
    /// </summary>
    [Fact]
    public async Task Migration_AppliesSuccessfullyOnPostgres()
    {
        await using var context = await CreateMigratedDbContextAsync();

        // Kiểm tra kết nối và khả năng truy vấn schema inventory
        bool canConnect = await context.Database.CanConnectAsync();
        Assert.True(canConnect);
        var tableCount = await context.Database.SqlQueryRaw<long>(
            "SELECT count(*)::bigint AS \"Value\" FROM information_schema.tables WHERE table_schema = 'inventory'").SingleAsync();
        Assert.Equal(9L, tableCount);
        Assert.Equal(3, (await context.Database.GetAppliedMigrationsAsync()).Count());

        // Kiểm tra sequence ReservationNumberSequence đã tồn tại
        var currentSequenceVal = await context.Database
            .SqlQueryRaw<long>("SELECT nextval('\"inventory\".\"ReservationNumberSequence\"') AS \"Value\"")
            .FirstAsync();

        Assert.True(currentSequenceVal > 0);
    }

    /// <summary>
    /// Kiểm thử 2: Ràng buộc toàn vẹn khóa ngoại (Foreign Key Restrict)
    /// Xóa Equipment có reservation hoặc block liên kết bị PostgreSQL từ chối để bảo toàn lịch sử.
    /// </summary>
    [Fact]
    public async Task ForeignKey_RestrictDeleteBehavior_PreventsCascadingDeletes()
    {
        await using var context = await CreateMigratedDbContextAsync();
        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var equipment = InventoryEquipment.Create(
            equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value;
        context.Equipments.Add(equipment);

        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var res = Reservation.CreateManual(
            Guid.NewGuid(), equipmentId, period, "Tester", "0900000000", now).Value;
        context.Reservations.Add(res);

        await context.SaveChangesAsync();

        // Cố gắng xóa Equipment trực tiếp qua SQL để kiểm chứng PostgreSQL FK constraint
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM inventory.equipments WHERE id = {equipmentId};");
        });

        // Bị chặn bởi PostgreSQL Foreign Key Restrict constraint
        Assert.Contains("FK_reservations_equipments_equipment_id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kiểm thử 3: Check Constraints của PostgreSQL ngăn chặn dữ liệu vi phạm tại tầng database.
    /// Kiểm tra ràng buộc thời lượng thuê tối đa 30 ngày (ck_reservations_rental_period_duration).
    /// </summary>
    [Fact]
    public async Task CheckConstraint_RentalDuration_EnforcedByPostgres()
    {
        await using var context = await CreateMigratedDbContextAsync();
        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var equipment = InventoryEquipment.Create(
            equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value;
        context.Equipments.Add(equipment);
        await context.SaveChangesAsync();

        // Cố tình insert trực tiếp bằng SQL vi phạm check constraint thời lượng thuê (40 ngày)
        Guid invalidResId = Guid.NewGuid();
        string sql = $"""
            INSERT INTO inventory.reservations (
                id, equipment_id, source_type, status, start_date, end_date,
                created_at, updated_at, delivery_photo_ids, return_photo_ids
            ) VALUES (
                '{invalidResId}', '{equipmentId}', 1, 2, '2026-11-01', '2026-12-10',
                NOW(), NOW(), ARRAY[]::uuid[], ARRAY[]::uuid[]
            );
            """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await context.Database.ExecuteSqlRawAsync(sql);
        });

        Assert.Contains("ck_reservations_rental_period_duration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kiểm thử 4: Check Constraints của PostgreSQL kiểm soát số lượng ảnh tối đa 5 ảnh (cardinality &lt;= 5).
    /// </summary>
    [Fact]
    public async Task CheckConstraint_MaxPhotos_EnforcedByPostgres()
    {
        await using var context = await CreateMigratedDbContextAsync();
        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var equipment = InventoryEquipment.Create(
            equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value;
        context.Equipments.Add(equipment);
        await context.SaveChangesAsync();

        // Cố tình insert trực tiếp 6 ảnh vào delivery_photo_ids
        Guid resId = Guid.NewGuid();
        string sixUuids = string.Join(",", Enumerable.Range(0, 6).Select(_ => $"'{Guid.NewGuid()}'::uuid"));
        string sql = $"""
            INSERT INTO inventory.reservations (
                id, equipment_id, source_type, status, start_date, end_date,
                created_at, updated_at, delivery_photo_ids, return_photo_ids
            ) VALUES (
                '{resId}', '{equipmentId}', 1, 2, '2026-11-01', '2026-11-05',
                NOW(), NOW(), ARRAY[{sixUuids}], ARRAY[]::uuid[]
            );
            """;

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await context.Database.ExecuteSqlRawAsync(sql);
        });

        Assert.Contains("ck_reservations_delivery_photos_count", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kiểm thử 5: Unique Filtered Index cho (SourceSystem, ExternalReference) khi external_reference khác null.
    /// Ngăn chặn trùng lặp mã đơn external trên toàn bộ lịch sử hệ thống.
    /// </summary>
    [Fact]
    public async Task UniqueIndex_SourceSystemExternalReference_PreventsDuplicates()
    {
        await using var context = await CreateMigratedDbContextAsync();
        Guid eqA = Guid.NewGuid();
        Guid eqB = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        context.Equipments.Add(InventoryEquipment.Create(eqA, orgId, CatalogLifecycleStatus.Active, true, now).Value);
        context.Equipments.Add(InventoryEquipment.Create(eqB, orgId, CatalogLifecycleStatus.Active, true, now).Value);
        await context.SaveChangesAsync();

        const string sourceSystem = "partner_zalopay";
        string externalRef = $"ORDER_EXT_{Guid.NewGuid():N}";

        var period = new RentalPeriod(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 5));
        var res1 = Reservation.CreateHold(
            Guid.NewGuid(), eqA, period, now.AddHours(2), now, sourceSystem, externalRef, renterAccountId: Guid.NewGuid()).Value;
        context.Reservations.Add(res1);
        await context.SaveChangesAsync();

        // Lượt thứ hai với cùng SourceSystem và ExternalReference trên thiết bị khác
        var res2 = Reservation.CreateHold(
            Guid.NewGuid(), eqB, period, now.AddHours(2), now, sourceSystem, externalRef, renterAccountId: Guid.NewGuid()).Value;
        context.Reservations.Add(res2);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await context.SaveChangesAsync();
        });

        Assert.Contains("ix_reservations_source_system_external_reference", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kiểm thử 10: Phát hiện stale updates thông qua Concurrency Token xmin của PostgreSQL.
    /// Khi hai context cùng đọc một Equipment và một context cập nhật trước,
    /// context thứ hai lưu sẽ bị ném DbUpdateConcurrencyException do xmin đã thay đổi.
    /// </summary>
    [Fact]
    public async Task ConcurrencyToken_Xmin_DetectsStaleUpdates()
    {
        await using var context1 = await CreateMigratedDbContextAsync();
        await using var context2 = await CreateMigratedDbContextAsync();

        Guid equipmentId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var equipment = InventoryEquipment.Create(
            equipmentId, organizationId, CatalogLifecycleStatus.Active, true, now).Value;
        context1.Equipments.Add(equipment);
        await context1.SaveChangesAsync();

        // Cả 2 context cùng đọc bản ghi thiết bị
        var eqContext1 = await context1.Equipments.FirstAsync(e => e.Id == equipmentId);
        var eqContext2 = await context2.Equipments.FirstAsync(e => e.Id == equipmentId);

        // Context 1 cập nhật thành công (xmin thay đổi trên Postgres)
        eqContext1.DisableBookings("Bảo trì nội bộ", DateTimeOffset.UtcNow);
        await context1.SaveChangesAsync();

        // Context 2 cố gắng cập nhật dựa trên xmin cũ
        eqContext2.EnableBookings(DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () =>
        {
            await context2.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Kiểm thử 11: Cô lập phạm vi dữ liệu theo tổ chức (Tenant Isolation).
    /// Tổ chức khác không thể truy xuất thiết bị hoặc lượt đặt lịch của tổ chức sở hữu qua các scoped read queries.
    /// </summary>
    [Fact]
    public async Task ScopedReads_EnforceTenantIsolation()
    {
        using var sp = BuildServiceProvider();
        await using var context = await CreateMigratedDbContextAsync();

        Guid orgA = Guid.NewGuid();
        Guid orgB = Guid.NewGuid();
        Guid eqId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var equipment = InventoryEquipment.Create(
            eqId, orgA, CatalogLifecycleStatus.Active, true, now).Value;
        context.Equipments.Add(equipment);

        var period = new RentalPeriod(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 5));
        var res = Reservation.CreateManual(Guid.NewGuid(), eqId, period, "OrgA Renter", "0900000000", now).Value;
        context.Reservations.Add(res);
        await context.SaveChangesAsync();

        var eqRepo = sp.GetRequiredService<IInventoryEquipmentRepository>();
        var resRepo = sp.GetRequiredService<IReservationRepository>();

        // Tổ chức B đọc thiết bị của Tổ chức A -> trả về null
        var eqForOrgB = await eqRepo.GetByIdForOrganizationAsync(eqId, orgB);
        Assert.Null(eqForOrgB);

        // Tổ chức B đọc reservation của Tổ chức A -> trả về null
        var resForOrgB = await resRepo.GetByIdForOrganizationAsync(res.Id, orgB);
        Assert.Null(resForOrgB);

        // Tổ chức A đọc dữ liệu của chính mình -> thành công
        var eqForOrgA = await eqRepo.GetByIdForOrganizationAsync(eqId, orgA);
        var resForOrgA = await resRepo.GetByIdForOrganizationAsync(res.Id, orgA);
        Assert.NotNull(eqForOrgA);
        Assert.NotNull(resForOrgA);
    }

}
