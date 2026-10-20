using System.Data.Common;
using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Domain.IRepositories;
using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Integration;

public sealed partial class PostgresEquipmentListingContentAndSearchTests
{
    [Fact]
    public async Task GetListingStatisticsAsync_MultiUnitListing_CountsAsSingleListing()
    {
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        await using var db = CreateDbContext();
        var repo = new EquipmentListingRepository(db);

        // Insert 1 listing with 5 physical units
        var listing = await IntegrationTestHelper.InsertTestListingAsync(
            db,
            ownerId: ownerId,
            eqTypeId: typeId,
            brandId: brandId,
            status: EquipmentListingStatus.Active,
            initialUnits: 5);

        var stats = await repo.GetListingStatisticsAsync(
            new EquipmentListingStatisticsCriteria(ownerId, EquipmentListingStatus.Active));

        Assert.Equal(1, stats.ByEquipmentType[typeId]);
        Assert.Equal(1, stats.ByBrand[brandId]);
    }

    [Fact]
    public async Task GetListingStatisticsAsync_SingleQuerySnapshot_BothDimensionsMatchExactSameTotal()
    {
        var typeId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();


        var gate = new StatisticsReadGate();
        var options = new DbContextOptionsBuilder<InventoryServiceDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(gate).Options;
        await using var dbWriter = CreateDbContext();
        await using var dbReader = new InventoryServiceDbContext(options);
        await IntegrationTestHelper.InsertTestListingAsync(dbWriter, ownerId: ownerId,
            eqTypeId: typeId, brandId: brandId, initialUnits: 2);
        var read = new EquipmentListingRepository(dbReader).GetListingStatisticsAsync(
            new EquipmentListingStatisticsCriteria(ownerId, EquipmentListingStatus.Active));
        try
        {
            await gate.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await IntegrationTestHelper.InsertTestListingAsync(dbWriter, ownerId: ownerId,
                eqTypeId: typeId, brandId: brandId, initialUnits: 1);
        }
        finally
        {
            gate.WriterCommitted.TrySetResult();
        }
        var stats = await read;
        Assert.Equal(1, gate.SelectCount);
        Assert.Equal(1, stats.ByEquipmentType[typeId]);
        Assert.Equal(1, stats.ByBrand[brandId]);
        var after = await new EquipmentListingRepository(dbWriter).GetListingStatisticsAsync(
            new EquipmentListingStatisticsCriteria(ownerId, EquipmentListingStatus.Active));
        Assert.Equal(2, after.ByEquipmentType[typeId]);
    }

    private sealed class StatisticsReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriterCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SelectCount { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("GROUP BY", StringComparison.Ordinal))
            {
                SelectCount++;
                ReadStarted.TrySetResult();
                await WriterCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}
