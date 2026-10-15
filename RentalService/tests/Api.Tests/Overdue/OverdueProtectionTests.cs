using Dprental.RentalService.Api.Tests.Returns;
using Dprental.RentalService.Api.Tests.TestHelpers;
using Dprental.RentalService.Application.Clients;
using Dprental.RentalService.Application.DTOs.Inventory;
using Dprental.RentalService.Application.DTOs.Operations;
using Dprental.RentalService.Application.IServices.Operations;
using Dprental.RentalService.Application.IServices.Overdue;
using Dprental.RentalService.Application.Services.Operations;
using Dprental.RentalService.Application.Services.Overdue;
using Dprental.RentalService.Common;
using Dprental.RentalService.Common.Enums;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.IRepositories;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Overdue;

public sealed class OverdueProtectionTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 16, 59, 59, TimeSpan.Zero));
    private readonly Mock<IInventoryRentalClient> _inventory = new();
    private string _connection = "";
    private string _database = "";

    public async Task InitializeAsync()
    {
        (_connection, _database) = await PostgresTestDatabaseHelper.CreateTestDatabaseAsync();
        await using var db = NewDb();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(PostgresTestDatabaseHelper.BaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private RentalServiceDbContext NewDb() => new(PostgresTestDatabaseHelper.CreateOptions(_connection));

    private ServiceProvider Provider()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _connection
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IInventoryRentalClient>(_inventory.Object);
        services.AddSingleton<IPaymentRentalClient>(Mock.Of<IPaymentRentalClient>());
        services.AddSingleton<IOrderLockManager>(new PostgresOrderLockManager(configuration, NullLogger<PostgresOrderLockManager>.Instance));
        services.AddDbContext<RentalServiceDbContext>(options => options.UseNpgsql(_connection));
        services.AddScoped<IRentalOrderRepository, RentalOrderRepository>();
        services.AddScoped<IOperationReceiptRepository, OperationReceiptRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<RentalServiceDbContext>());
        services.AddScoped<IRentalOperationExecutor, RentalOperationExecutor>();
        services.AddScoped<IOverdueProtectionService, OverdueProtectionService>();
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private async Task<RentalOrder> SeedAsync(bool returnFirst = false)
    {
        var order = ReturnTests.NewOrder();
        var item = order.Items.Single();
        if (returnFirst)
        {
            var first = item.UnitAssignments.First();
            var result = item.RecordAssetReturns(item.SupplierId,
                new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero), null,
                [new ReturnUnitData(first.Id, UnitCondition.Normal, null, [])],
                new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero), item.SupplierId);
            Assert.True(result.IsSuccess);
        }
        await using var db = NewDb();
        db.RentalOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task BoundaryAndPartialReturn_OnlyUnreturnedUnitIsProtected()
    {
        var order = await SeedAsync(returnFirst: true);
        var item = order.Items.Single();
        var unreturned = item.UnitAssignments.Single(u => u.ReturnRecordId is null);
        _inventory.Setup(x => x.MarkAssetsOverdueAsync(item.AllocationGroupId,
                It.IsAny<MarkAssetsOverdueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, MarkAssetsOverdueRequest request, CancellationToken _) =>
                Result.Success(new MarkAssetsOverdueResponse("receipt", request.OperationKey, group,
                    _clock.GetUtcNow(), false,
                    [new OverdueUnitDto(unreturned.AllocationId, unreturned.AssetId, "PROTECTED", "protect-ref")])));
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOverdueProtectionService>();
        Assert.Empty(await service.GetDueOrderIdsAsync(10));
        _clock.Advance(TimeSpan.FromSeconds(1)); // 00:00 VN on Oct 16
        Assert.Equal([order.Id], await service.GetDueOrderIdsAsync(10));
        var result = await service.ProcessOrderAsync(order.Id);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(0, result.Failed);
        _inventory.Verify(x => x.MarkAssetsOverdueAsync(item.AllocationGroupId,
            It.Is<MarkAssetsOverdueRequest>(r => r.AllocationIds.Count == 1 &&
                r.AllocationIds[0] == unreturned.AllocationId), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(await service.GetDueOrderIdsAsync(10));
        await using var db = NewDb();
        var receipt = await db.RentalOperationReceipts.SingleAsync(r => r.Action == RentalItem.OverdueProtectionAction);
        Assert.Equal(RentalOperationReceipt.StatusSucceeded, receipt.Status);
        Assert.Contains("protect-ref", receipt.ResponseJson);
    }

    [Fact]
    public async Task LostInventoryReply_RetryUsesSameProviderKey()
    {
        var order = await SeedAsync(returnFirst: true);
        var item = order.Items.Single();
        var unreturned = item.UnitAssignments.Single(u => u.ReturnRecordId is null);
        var keys = new List<string>();
        _inventory.Setup(x => x.MarkAssetsOverdueAsync(item.AllocationGroupId,
                It.IsAny<MarkAssetsOverdueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, MarkAssetsOverdueRequest request, CancellationToken _) =>
            {
                keys.Add(request.OperationKey);
                return keys.Count == 1
                    ? Result.Failure<MarkAssetsOverdueResponse>(Error.ServiceUnavailable("INVENTORY_TIMEOUT", "timeout"))
                    : Result.Success(new MarkAssetsOverdueResponse("receipt", request.OperationKey, group,
                        _clock.GetUtcNow(), true,
                        [new OverdueUnitDto(unreturned.AllocationId, unreturned.AssetId, "PROTECTED", "protect-ref")]));
            });
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var provider = Provider();
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IOverdueProtectionService>();
            var first = await service.ProcessOrderAsync(order.Id);
            Assert.Equal(1, first.Failed);
            Assert.Empty(await service.GetDueOrderIdsAsync(10));
        }
        _clock.Advance(TimeSpan.FromMinutes(2));
        using (var scope = provider.CreateScope())
        {
            var pending = await scope.ServiceProvider.GetRequiredService<IOperationReceiptRepository>()
                .GetPendingOperationDescriptorsAsync(_clock.GetUtcNow(), 10);
            Assert.Single(pending);
            var result = await scope.ServiceProvider.GetRequiredService<IRentalOperationExecutor>()
                .ExecuteScheduledOperationAsync(pending.Single());
            Assert.True(result.IsSuccess, result.Error.Message);
        }
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        await using var db = NewDb();
        Assert.Equal(RentalOperationReceipt.StatusSucceeded,
            (await db.RentalOperationReceipts.SingleAsync(r => r.Action == RentalItem.OverdueProtectionAction)).Status);
    }

    [Fact]
    public async Task ReturnAfterScheduling_SkipsInventoryCall()
    {
        var seeded = await SeedAsync();
        var seededItem = seeded.Items.Single();
        var first = seededItem.UnitAssignments.First();
        _clock.Advance(TimeSpan.FromSeconds(1));

        string operationId = $"overdue:{first.Id}:g0";
        string providerKey = RentalOperationExecutor.ComputeOperationKey(
            RentalItem.OverdueProtectionAction, first.Id.ToString("D"));
        string payload = JsonSerializer.Serialize(new OverdueProtectionCommand(first.Id,
            new MarkAssetsOverdueRequest([first.AllocationId], providerKey)));
        Guid receiptId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            var order = await new RentalOrderRepository(db).GetByIdWithDetailsAsync(seeded.Id);
            Assert.NotNull(order);
            order.AddOperationReceipt(RentalOperationReceipt.CreatePending(receiptId, order.Id,
                operationId, RentalItem.OverdueProtectionAction,
                RentalOperationExecutor.ComputeFingerprint(payload), _clock.GetUtcNow(),
                targetItemId: seededItem.Id, payloadJson: payload));
            var item = order.Items.Single();
            var returned = item.RecordAssetReturns(item.SupplierId,
                _clock.GetUtcNow(), null,
                [new ReturnUnitData(first.Id, UnitCondition.Normal, null, [])],
                _clock.GetUtcNow(), item.SupplierId);
            Assert.True(returned.IsSuccess);
            order.IncrementVersion();
            await new RentalOrderRepository(db).UpdateAsync(order);
            await db.SaveChangesAsync();
        }

        using var provider = Provider();
        using var scope = provider.CreateScope();
        var descriptor = new OperationReceiptDescriptor(receiptId, seeded.Id, operationId,
            RentalItem.OverdueProtectionAction, seededItem.Id,
            RentalOperationReceipt.StatusPending, 0, null, _clock.GetUtcNow(), null, false);
        var result = await scope.ServiceProvider.GetRequiredService<IRentalOperationExecutor>()
            .ExecuteScheduledOperationAsync(descriptor);
        Assert.True(result.IsSuccess, result.Error.Message);
        _inventory.Verify(x => x.MarkAssetsOverdueAsync(It.IsAny<string>(),
            It.IsAny<MarkAssetsOverdueRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        await using var verify = NewDb();
        Assert.Equal(RentalOperationReceipt.StatusSucceeded,
            (await verify.RentalOperationReceipts.SingleAsync(r => r.Id == receiptId)).Status);
    }

    [Fact]
    public async Task ExhaustedReceipt_NewGenerationKeepsInventoryKey()
    {
        var seeded = await SeedAsync(returnFirst: true);
        var item = seeded.Items.Single();
        var unit = item.UnitAssignments.Single(u => u.ReturnRecordId is null);
        _clock.Advance(TimeSpan.FromSeconds(1));
        string key = RentalOperationExecutor.ComputeOperationKey(
            RentalItem.OverdueProtectionAction, unit.Id.ToString("D"));
        await using (var db = NewDb())
        {
            var order = await new RentalOrderRepository(db).GetByIdWithDetailsAsync(seeded.Id);
            Assert.NotNull(order);
            order.AddOperationReceipt(new RentalOperationReceipt(Guid.NewGuid(), order.Id,
                $"overdue:{unit.Id}:g0", RentalItem.OverdueProtectionAction, "old", false,
                "INVENTORY_TIMEOUT", "timeout", _clock.GetUtcNow(),
                status: RentalOperationReceipt.StatusFailed, targetItemId: item.Id,
                errorClassification: RentalOperationReceipt.ClassificationTransientExhausted));
            await new RentalOrderRepository(db).UpdateAsync(order);
            await db.SaveChangesAsync();
        }
        _inventory.Setup(x => x.MarkAssetsOverdueAsync(item.AllocationGroupId,
                It.IsAny<MarkAssetsOverdueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string group, MarkAssetsOverdueRequest request, CancellationToken _) =>
                Result.Success(new MarkAssetsOverdueResponse("receipt", request.OperationKey, group,
                    _clock.GetUtcNow(), true,
                    [new OverdueUnitDto(unit.AllocationId, unit.AssetId, "PROTECTED", "protect-ref")])));

        using var provider = Provider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOverdueProtectionService>();
        Assert.Equal(seeded.Id, Assert.Single(await service.GetDueOrderIdsAsync(10)));
        var result = await service.ProcessOrderAsync(seeded.Id);
        Assert.Equal(1, result.Succeeded);
        _inventory.Verify(x => x.MarkAssetsOverdueAsync(item.AllocationGroupId,
            It.Is<MarkAssetsOverdueRequest>(r => r.OperationKey == key), It.IsAny<CancellationToken>()), Times.Once);
        await using var verify = NewDb();
        Assert.Equal(2, await verify.RentalOperationReceipts.CountAsync(r => r.Action == RentalItem.OverdueProtectionAction));
    }
}
