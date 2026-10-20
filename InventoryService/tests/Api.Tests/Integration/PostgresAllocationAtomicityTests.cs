using Dprental.InventoryService.Api.Tests.Security;
using Dprental.InventoryService.Application.DTOs.Allocations;
using Dprental.InventoryService.Application.IServices;
using Dprental.InventoryService.Application.Security;
using Dprental.InventoryService.Application.Services;
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
/// Kiểm thử tính nguyên tử (Atomicity) và bảo vệ giao dịch (Transactional Rollback)
/// của Hold, Allocate và Transactional Outbox trên cơ sở dữ liệu PostgreSQL 17 thật theo DPR-151.
/// </summary>
public sealed class PostgresAllocationAtomicityTests : IClassFixture<PostgresFixture>
{
    private readonly string _connectionString;
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly FixedToday = BusinessDateTime.ToVietnamDate(FixedNow);

    public PostgresAllocationAtomicityTests(PostgresFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
    }

    private ServiceProvider BuildServiceProvider(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInventoryServiceDomainRepositories(_connectionString);
        services.AddSingleton(timeProvider ?? new TestTimeProvider(FixedNow));
        services.AddScoped<IInventoryAllocationService, InventoryAllocationService>();
        return services.BuildServiceProvider();
    }

    private InventoryServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_connectionString, options => options.MigrationsHistoryTable(InventoryServiceDbContext.MigrationsHistoryTableName, InventoryServiceDbContext.SchemaName))
            .Options;
        return new InventoryServiceDbContext(options);
    }

    private async Task<InventoryAsset> SeedAssetAsync(Guid ownerUserId)
    {
        await using var db = CreateDbContext();
        return await IntegrationTestHelper.InsertTestAssetAsync(db, ownerId: ownerUserId, now: FixedNow);
    }

    private sealed class ServiceCallerContext : IInventoryCallerContext
    {
        public bool IsAuthenticated => true;
        public bool IsService => true;
        public bool IsAdmin => false;
        public Guid? UserId => null;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public IReadOnlySet<string> Scopes => new HashSet<string> { InventoryAccessEvaluator.ScopeAllocationWrite };
        public IReadOnlySet<string> Roles => new HashSet<string>();
        public Guid? OrganizationId => null;
    }

    [Fact]
    public async Task Atomicity_Hold_OutboxInsertFailure_RollsBackHoldAndTransition()
    {
        using var sp = BuildServiceProvider();
        var allocationService = sp.GetRequiredService<IInventoryAllocationService>();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        // Ép lỗi bằng database trigger trên PostgreSQL cho outbox_messages
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        string triggerFuncName = $"trg_fail_outbox_hold_{asset.Id:N}";
        string triggerName = $"trg_check_hold_{asset.Id:N}";

        try
        {
            string createTriggerSql = $@"
                CREATE OR REPLACE FUNCTION {triggerFuncName}()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.kafka_key = '{asset.Id:D}' THEN
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

            var period = new RentalPeriod(FixedToday.AddDays(5), FixedToday.AddDays(8));
            var req = new CreateHoldRequest("rental-sys", "REF-FAIL-OUTBOX", period.StartDate, period.EndDate, FixedNow.AddHours(2));

            // Thao tác tạo hold bị ép lỗi khi insert outbox message
            var result = await allocationService.CreateHoldAsync(asset.Id, req, caller);

            Assert.True(result.IsFailure);

            // Xác minh database: cả Hold, transition và outbox message đều bị rollback sạch sẽ
            await using var db = CreateDbContext();
            var allocations = await db.AssetAllocations.Where(a => a.AssetId == asset.Id).ToListAsync();
            Assert.Empty(allocations);

            var outboxMessages = await db.OutboxMessages.Where(m => m.KafkaKey == asset.Id.ToString("D")).ToListAsync();
            Assert.Empty(outboxMessages);
        }
        finally
        {
            string dropSql = $@"
                DROP TRIGGER IF EXISTS {triggerName} ON inventory.outbox_messages;
                DROP FUNCTION IF EXISTS {triggerFuncName}();
            ";
            await using var dropCmd = new NpgsqlCommand(dropSql, connection);
            await dropCmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Atomicity_Allocate_TransitionInsertFailure_RollsBackAllocate()
    {
        using var sp = BuildServiceProvider();
        var allocationService = sp.GetRequiredService<IInventoryAllocationService>();

        var ownerId = Guid.NewGuid();
        var asset = await SeedAssetAsync(ownerId);
        var caller = new ServiceCallerContext();

        // 1. Tạo Hold thành công
        var period = new RentalPeriod(FixedToday.AddDays(10), FixedToday.AddDays(15));
        var createRes = await allocationService.CreateHoldAsync(
            asset.Id,
            new CreateHoldRequest("rental-sys", "REF-FAIL-TRANSITION", period.StartDate, period.EndDate, FixedNow.AddHours(2)),
            caller);
        Assert.True(createRes.IsSuccess);
        var allocationId = createRes.Value.Allocation.Id;

        // 2. Ép lỗi bằng database trigger trên PostgreSQL cho allocation_transitions khi insert transition Allocate
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        string triggerFuncName = $"trg_fail_trans_{allocationId:N}";
        string triggerName = $"trg_check_trans_{allocationId:N}";

        try
        {
            string createTriggerSql = $@"
                CREATE OR REPLACE FUNCTION {triggerFuncName}()
                RETURNS TRIGGER AS $$
                BEGIN
                    IF NEW.allocation_id = '{allocationId:D}' AND NEW.operation_type = 3 THEN
                        RAISE EXCEPTION 'Simulated transition insert failure for allocation %', '{allocationId:D}';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON inventory.allocation_transitions
                FOR EACH ROW
                EXECUTE FUNCTION {triggerFuncName}();
            ";

            await using (var cmd = new NpgsqlCommand(createTriggerSql, connection))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Gọi Allocate
            var allocResult = await allocationService.AllocateAsync(allocationId, new AllocateRequest("OP-FAIL-TRANS"), caller);

            Assert.True(allocResult.IsFailure);

            // 4. Xác minh database: Allocation vẫn ở trạng thái Held, không bị đổi sang Allocated, và chỉ có đúng 1 transition
            await using var db = CreateDbContext();
            var allocation = await db.AssetAllocations
                .Include(a => a.Transitions)
                .SingleOrDefaultAsync(a => a.Id == allocationId);

            Assert.NotNull(allocation);
            Assert.Equal(AllocationStatus.Held, allocation.Status);
            Assert.Single(allocation.Transitions);
            Assert.Equal(AllocationOperationType.CreateHold, allocation.Transitions.First().OperationType);
        }
        finally
        {
            string dropSql = $@"
                DROP TRIGGER IF EXISTS {triggerName} ON inventory.allocation_transitions;
                DROP FUNCTION IF EXISTS {triggerFuncName}();
            ";
            await using var dropCmd = new NpgsqlCommand(dropSql, connection);
            await dropCmd.ExecuteNonQueryAsync();
        }
    }
}
