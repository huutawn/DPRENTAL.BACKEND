using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Calendar;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Services;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.Repositories;
using Dprental.InventoryService.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

/// <summary>
/// Kiểm thử tính nguyên tử (Atomicity) và bảo vệ giao dịch (Transactional Boundary / Rollback)
/// của CalendarBlock và Transactional Outbox trên cơ sở dữ liệu PostgreSQL thật theo DPR-149.
/// </summary>
public sealed class PostgresCalendarAtomicityTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);

    public PostgresCalendarAtomicityTests(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private ServiceProvider BuildServiceProvider(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        services.AddSingleton(timeProvider ?? new TestTimeProvider(FixedNow));
        services.AddScoped<CalendarService>();
        return services.BuildServiceProvider();
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId, Guid orgId, bool acceptingBookings = true)
    {
        await using var db = CreateDbContext();
        return await IntegrationTestHelper.InsertTestAssetAsync(db, ownerId: ownerUserId, acceptingBookings: acceptingBookings, now: FixedNow);
    }

    private sealed class TestCallerContext(Guid userId, Guid? orgId = null) : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => false;
        public bool IsAdmin => false;
        public Guid? UserId => userId;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string>();
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => orgId;
    }

    [Fact]
    public async Task OverlapWithBlock_ActiveHold_OrAllocated_ReturnsConflict_AndCreatesNoEvent()
    {
        using var sp = BuildServiceProvider();
        var calendarService = sp.GetRequiredService<CalendarService>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var period1 = new RentalPeriod(FixedToday.AddDays(10), FixedToday.AddDays(12));
        var period2 = new RentalPeriod(FixedToday.AddDays(15), FixedToday.AddDays(18));
        var period3 = new RentalPeriod(FixedToday.AddDays(20), FixedToday.AddDays(25));

        // 1. Seed existing block, active hold, allocated
        await using (var db = CreateDbContext())
        {
            var block = CalendarBlock.Create(
                Guid.NewGuid(), asset.Id, period1.StartDate, period1.EndDate,
                CalendarBlockReason.Maintenance, ownerId.ToString("D"), FixedNow, FixedToday).Value;
            var activeHold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, period2, FixedNow.AddHours(2), FixedNow, "rental", "EXT-HOLD-1").Value;
            var allocated = AssetAllocation.CreateManual(
                Guid.NewGuid(), asset.Id, period3, FixedNow, "EXT-ALLOC-1").Value;

            db.CalendarBlocks.Add(block);
            db.AssetAllocations.AddRange(activeHold, allocated);
            await db.SaveChangesAsync();
        }

        int initialOutboxCount;
        await using (var db = CreateDbContext())
        {
            initialOutboxCount = await db.OutboxMessages.CountAsync();
        }

        // A. Overlap với Block -> ASSET_BLOCKED
        var req1 = new CreateCalendarBlockRequest { StartDate = period1.StartDate, EndDate = period1.EndDate, Reason = CalendarBlockReason.Other };
        var res1 = await calendarService.CreateCalendarBlockAsync(asset.Id, req1, caller);
        Assert.True(res1.IsFailure);
        Assert.Equal("ASSET_BLOCKED", res1.Error.Code);

        // B. Overlap với Active Hold -> ASSET_UNAVAILABLE
        var req2 = new CreateCalendarBlockRequest { StartDate = period2.StartDate, EndDate = period2.EndDate, Reason = CalendarBlockReason.Maintenance };
        var res2 = await calendarService.CreateCalendarBlockAsync(asset.Id, req2, caller);
        Assert.True(res2.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", res2.Error.Code);

        // C. Overlap với Allocated -> ASSET_UNAVAILABLE
        var req3 = new CreateCalendarBlockRequest { StartDate = period3.StartDate, EndDate = period3.EndDate, Reason = CalendarBlockReason.Inspection };
        var res3 = await calendarService.CreateCalendarBlockAsync(asset.Id, req3, caller);
        Assert.True(res3.IsFailure);
        Assert.Equal("ASSET_UNAVAILABLE", res3.Error.Code);

        // Không tạo thêm outbox message nào
        await using (var db = CreateDbContext())
        {
            var finalOutboxCount = await db.OutboxMessages.CountAsync();
            Assert.Equal(initialOutboxCount, finalOutboxCount);
        }
    }

    [Fact]
    public async Task ExpiredHold_Released_AndCancelled_DoNotPreventBlockCreation()
    {
        using var sp = BuildServiceProvider();
        var calendarService = sp.GetRequiredService<CalendarService>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var sharedPeriod = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(8));

        // Seed: 1 Expired Hold, 1 Released allocation, 1 Cancelled allocation đều trùng sharedPeriod
        await using (var db = CreateDbContext())
        {
            var expiredHold = AssetAllocation.CreateHold(
                Guid.NewGuid(), asset.Id, sharedPeriod, FixedNow.AddHours(-1), FixedNow.AddHours(-5), "rental", "EXT-EXP").Value;

            var releasedAlloc = AssetAllocation.CreateManual(
                Guid.NewGuid(), asset.Id, sharedPeriod, FixedNow, "EXT-REL").Value;
            releasedAlloc.Release("Done", FixedNow.AddMinutes(5), "op");

            var cancelledAlloc = AssetAllocation.CreateManual(
                Guid.NewGuid(), asset.Id, sharedPeriod, FixedNow, "EXT-CAN").Value;
            cancelledAlloc.Cancel("Cancelled", FixedNow.AddMinutes(5), "op");

            db.AssetAllocations.AddRange(expiredHold, releasedAlloc, cancelledAlloc);
            await db.SaveChangesAsync();
        }

        // Tạo block trùng với sharedPeriod -> Thành công!
        var req = new CreateCalendarBlockRequest
        {
            StartDate = sharedPeriod.StartDate,
            EndDate = sharedPeriod.EndDate,
            Reason = CalendarBlockReason.Maintenance,
            Note = "Bảo trì sau khi các allocation trước đó đã hết hạn/hủy"
        };

        var res = await calendarService.CreateCalendarBlockAsync(asset.Id, req, caller);
        Assert.True(res.IsSuccess);

        await using (var db = CreateDbContext())
        {
            var savedBlock = await db.CalendarBlocks.SingleOrDefaultAsync(b => b.AssetId == asset.Id);
            Assert.NotNull(savedBlock);
            Assert.Equal(sharedPeriod.StartDate, savedBlock.StartDate);

            var outboxMsg = await db.OutboxMessages.SingleOrDefaultAsync(m => m.KafkaKey == asset.Id.ToString("D"));
            Assert.NotNull(outboxMsg);
        }
    }

    [Fact]
    public async Task Atomicity_CreateBlock_Succeeds_BlockAndEventBothExist()
    {
        using var sp = BuildServiceProvider();
        var calendarService = sp.GetRequiredService<CalendarService>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var req = new CreateCalendarBlockRequest
        {
            StartDate = FixedToday.AddDays(1),
            EndDate = FixedToday.AddDays(3),
            Reason = CalendarBlockReason.Inspection
        };

        var result = await calendarService.CreateCalendarBlockAsync(asset.Id, req, caller);
        Assert.True(result.IsSuccess);

        await using (var db = CreateDbContext())
        {
            var block = await db.CalendarBlocks.SingleOrDefaultAsync(b => b.AssetId == asset.Id);
            Assert.NotNull(block);

            var outbox = await db.OutboxMessages.SingleOrDefaultAsync(m => m.KafkaKey == asset.Id.ToString("D"));
            Assert.NotNull(outbox);
            Assert.Equal("inventory.asset.events.v1", outbox.Topic);
            Assert.Contains("InventoryAssetAvailabilityChanged", outbox.Payload);
        }
    }

    [Fact]
    public async Task Atomicity_DeleteBlock_Succeeds_BlockRemovedAndEventAdded()
    {
        using var sp = BuildServiceProvider();
        var calendarService = sp.GetRequiredService<CalendarService>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        var req = new CreateCalendarBlockRequest
        {
            StartDate = FixedToday.AddDays(1),
            EndDate = FixedToday.AddDays(3),
            Reason = CalendarBlockReason.Inspection
        };
        var createResult = await calendarService.CreateCalendarBlockAsync(asset.Id, req, caller);
        Assert.True(createResult.IsSuccess);

        // Xóa block
        var deleteResult = await calendarService.DeleteCalendarBlockAsync(asset.Id, createResult.Value.Id, caller);
        Assert.True(deleteResult.IsSuccess);

        await using (var db = CreateDbContext())
        {
            var block = await db.CalendarBlocks.SingleOrDefaultAsync(b => b.Id == createResult.Value.Id);
            Assert.Null(block);

            // Hai sự kiện outbox đã commit: 1 từ create, 1 từ delete
            var events = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Equal(2, events.Count);
        }
    }

    [Fact]
    public async Task Atomicity_OutboxTriggerFailure_RollsBackCreateBlock()
    {
        using var sp = BuildServiceProvider();
        var calendarService = sp.GetRequiredService<CalendarService>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        // Tạo database trigger ép PostgreSQL từ chối INSERT vào outbox_messages chỉ với KafkaKey của asset này
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        string triggerFuncName = $"trg_fail_outbox_{asset.Id:N}";
        string triggerName = $"trg_check_{asset.Id:N}";

        try
        {
            string createTriggerSql = $@"
                CREATE OR REPLACE FUNCTION {triggerFuncName}()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.kafka_key = '{asset.Id:D}' OR NEW.kafka_key LIKE '%:' || '{asset.Id:D}' THEN
                        RAISE EXCEPTION 'Simulated outbox insert failure for asset %', '{asset.Id:D}';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON inventory.outbox_messages
                FOR EACH ROW
                EXECUTE FUNCTION {triggerFuncName}();
            ";

            await using (var cmd = new NpgsqlCommand(createTriggerSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Gọi CreateCalendarBlockAsync -> Sẽ gặp lỗi khi save outbox trong cùng transaction
            var req = new CreateCalendarBlockRequest
            {
                StartDate = FixedToday.AddDays(5),
                EndDate = FixedToday.AddDays(8),
                Reason = CalendarBlockReason.Maintenance
            };

            var result = await calendarService.CreateCalendarBlockAsync(asset.Id, req, caller);

            // Phải trả về thất bại
            Assert.True(result.IsFailure);
            Assert.Equal(ErrorType.Failure, result.Error.Type);

            // Database KHÔNG có block mới và KHÔNG có outbox mới (atomic rollback)
            await using (var db = CreateDbContext())
            {
                var blocks = await db.CalendarBlocks.Where(b => b.AssetId == asset.Id).ToListAsync();
                Assert.Empty(blocks);

                var outbox = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
                Assert.Empty(outbox);
            }
        }
        finally
        {
            // Dọn dẹp trigger và function sau test
            string dropTriggerSql = $@"
                DROP TRIGGER IF EXISTS {triggerName} ON inventory.outbox_messages;
                DROP FUNCTION IF EXISTS {triggerFuncName}();
            ";
            await using var dropCmd = new NpgsqlCommand(dropTriggerSql, connection);
            await dropCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Atomicity_OutboxTriggerFailure_RollsBackDeleteBlock()
    {
        using var sp = BuildServiceProvider();
        var calendarService = sp.GetRequiredService<CalendarService>();

        var ownerId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId, orgId);
        var caller = new TestCallerContext(ownerId, orgId);

        // 1. Tạo block khi trigger chưa kích hoạt
        var req = new CreateCalendarBlockRequest
        {
            StartDate = FixedToday.AddDays(5),
            EndDate = FixedToday.AddDays(8),
            Reason = CalendarBlockReason.Maintenance
        };
        var createResult = await calendarService.CreateCalendarBlockAsync(asset.Id, req, caller);
        Assert.True(createResult.IsSuccess);

        // 2. Kích hoạt trigger ép lỗi outbox
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        string triggerFuncName = $"trg_fail_del_outbox_{asset.Id:N}";
        string triggerName = $"trg_check_del_{asset.Id:N}";

        try
        {
            string createTriggerSql = $@"
                CREATE OR REPLACE FUNCTION {triggerFuncName}()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.kafka_key = '{asset.Id:D}' OR NEW.kafka_key LIKE '%:' || '{asset.Id:D}' THEN
                        RAISE EXCEPTION 'Simulated outbox insert failure on delete for asset %', '{asset.Id:D}';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON inventory.outbox_messages
                FOR EACH ROW
                EXECUTE FUNCTION {triggerFuncName}();
            ";

            await using (var cmd = new NpgsqlCommand(createTriggerSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Gọi DeleteCalendarBlockAsync -> Sẽ thất bại khi commit Outbox
            var deleteResult = await calendarService.DeleteCalendarBlockAsync(asset.Id, createResult.Value.Id, caller);
            Assert.True(deleteResult.IsFailure);
            Assert.Equal(ErrorType.Failure, deleteResult.Error.Type);

            // 4. Block VẪN TỒN TẠI do transaction đã bị rollback hoàn toàn
            await using (var db = CreateDbContext())
            {
                var blockStillExists = await db.CalendarBlocks.SingleOrDefaultAsync(b => b.Id == createResult.Value.Id);
                Assert.NotNull(blockStillExists);

                // Số lượng event chỉ có đúng 1 từ lúc create, không có event delete
                var events = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
                Assert.Single(events);
            }
        }
        finally
        {
            string dropTriggerSql = $@"
                DROP TRIGGER IF EXISTS {triggerName} ON inventory.outbox_messages;
                DROP FUNCTION IF EXISTS {triggerFuncName}();
            ";
            await using var dropCmd = new NpgsqlCommand(dropTriggerSql, connection);
            await dropCmd.ExecuteNonQueryAsync();
        }
    }
}
