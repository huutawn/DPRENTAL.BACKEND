using System.Reflection;
using Dprental.RentalService.Common;
using Dprental.RentalService.Domain.DependencyInjection;
using Dprental.RentalService.Domain.Entities;
using Dprental.RentalService.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Dprental.RentalService.Api.Tests.Domain;

public sealed class RentalItemSnapshotTests
{
    private readonly Guid _renterId = Guid.NewGuid();
    private readonly Guid _supplierId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 10, 9, 3, 0, 0, TimeSpan.Zero);

    private InitialRentalItemDto CreateItemDto(
        DateOnly start,
        DateOnly end,
        int quantity = 1,
        string pickupLocation = "District 1",
        RentalItemListingSnapshotDto? snapshot = null) =>
        new(
            _itemId,
            Guid.NewGuid(),
            _supplierId,
            quantity,
            start,
            end,
            "Sony A7 IV",
            null,
            pickupLocation,
            500_000m,
            2_000_000m,
            "rental-item:group-1",
            Enumerable.Range(0, quantity)
                .Select(_ => new InitialRentalUnitAssignmentDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()))
                .ToList(),
            snapshot ?? TestListingSnapshots.Default);

    private Result<RentalOrder> CreateOrder(InitialRentalItemDto itemDto) =>
        RentalOrder.Create(
            Guid.NewGuid(),
            "DH-2026-000001",
            _renterId,
            "VND",
            "Delivery",
            "Nguyen Van A",
            "0901234567",
            "123 Nguyen Hue, District 1",
            null,
            "bank-ref-001",
            "v1.0",
            "terms/v1.0.pdf",
            "hash123456",
            "op-checkout-1",
            _now,
            [itemDto]);

    [Fact]
    public void CreateOrder_StoresListingSnapshotOnRentalItem()
    {
        RentalItemListingSnapshotDto snapshot = TestListingSnapshots.Default;

        RentalItem item = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 14), quantity: 2)).Value.Items.Single();

        Assert.Equal(snapshot.EquipmentTypeId, item.EquipmentTypeId);
        Assert.Equal(snapshot.EquipmentTypeName, item.EquipmentTypeNameSnapshot);
        Assert.Equal(snapshot.BrandId, item.BrandId);
        Assert.Equal(snapshot.BrandName, item.BrandNameSnapshot);
        Assert.Equal(snapshot.Model, item.ModelSnapshot);
        Assert.Equal(snapshot.SpecSchemaVersion, item.SpecSchemaVersionSnapshot);
        Assert.Equal(snapshot.SpecValuesJson, item.SpecValuesSnapshot);
        Assert.Equal(snapshot.ThumbnailMediaId, item.ThumbnailMediaIdSnapshot);
        Assert.Equal("Sony A7 IV", item.ListingTitleSnapshot);
        Assert.Equal(_supplierId, item.SupplierId);
        Assert.Equal(500_000m, item.DailyRateSnapshot);
        Assert.Equal(2_000_000m, item.DepositPerUnitSnapshot);
        Assert.Equal(2, item.Quantity);
    }

    [Fact]
    public void CreateOrder_PricesRentalDaysWithExclusiveEndDate()
    {
        RentalItem item = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 15), quantity: 2)).Value.Items.Single();

        Assert.Equal(3, item.RentalDays);
        Assert.Equal(500_000m * 2 * 3, item.RentalAmount);
        Assert.Equal(2_000_000m * 2, item.DepositAmount);
    }

    [Fact]
    public void CreateOrder_AllowsSingleDayRental()
    {
        Result<RentalOrder> result = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 13)));

        Assert.True(result.IsSuccess);
        RentalItem item = result.Value.Items.Single();
        Assert.Equal(1, item.RentalDays);
        Assert.Equal(500_000m, item.RentalAmount);
    }

    [Fact]
    public void CreateOrder_RejectsEndDateNotAfterStartDate()
    {
        Result<RentalOrder> result = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 12)));

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidRentalPeriod, result.Error);
    }

    public static TheoryData<RentalItemListingSnapshotDto> InvalidSnapshots()
    {
        RentalItemListingSnapshotDto valid = TestListingSnapshots.Default;
        return
        [
            valid with { EquipmentTypeId = Guid.Empty },
            valid with { EquipmentTypeName = " " },
            valid with { BrandId = Guid.Empty },
            valid with { BrandName = "" },
            valid with { Model = new string('x', 201) },
            valid with { SpecSchemaVersion = 0 },
            valid with { SpecValuesJson = "not-json" },
            valid with { SpecValuesJson = "[1,2,3]" },
            valid with { ThumbnailMediaId = Guid.Empty }
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void CreateOrder_RejectsInvalidListingSnapshot(RentalItemListingSnapshotDto snapshot)
    {
        Result<RentalOrder> result = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 14), snapshot: snapshot));

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidSnapshot, result.Error);
    }

    [Fact]
    public void CreateOrder_RejectsMissingListingSnapshot()
    {
        InitialRentalItemDto itemDto = CreateItemDto(new(2026, 10, 12), new(2026, 10, 14)) with { ListingSnapshot = null! };

        Result<RentalOrder> result = CreateOrder(itemDto);

        Assert.True(result.IsFailure);
        Assert.Equal(RentalErrors.InvalidSnapshot, result.Error);
    }

    [Fact]
    public void Snapshot_StaysUnchanged_AfterLifecycleTransitions()
    {
        RentalOrder order = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 14), quantity: 2)).Value;
        RentalItem item = order.Items.Single();
        var before = CaptureSnapshot(item);

        Assert.True(order.MarkAsPaid("INV-001", "PAY-001", _now.AddMinutes(5), _now.AddMinutes(5)).IsSuccess);

        Assert.Equal(before, CaptureSnapshot(item));
    }

    [Fact]
    public void SnapshotProperties_HaveNoPublicSetter()
    {
        string[] snapshotProperties =
        [
            nameof(RentalItem.EquipmentListingId),
            nameof(RentalItem.SupplierId),
            nameof(RentalItem.Quantity),
            nameof(RentalItem.ExpectedStartDate),
            nameof(RentalItem.ExpectedEndDate),
            nameof(RentalItem.ListingTitleSnapshot),
            nameof(RentalItem.EquipmentTypeId),
            nameof(RentalItem.EquipmentTypeNameSnapshot),
            nameof(RentalItem.BrandId),
            nameof(RentalItem.BrandNameSnapshot),
            nameof(RentalItem.ModelSnapshot),
            nameof(RentalItem.SpecSchemaVersionSnapshot),
            nameof(RentalItem.SpecValuesSnapshot),
            nameof(RentalItem.ThumbnailMediaIdSnapshot),
            nameof(RentalItem.DailyRateSnapshot),
            nameof(RentalItem.DepositPerUnitSnapshot),
            nameof(RentalItem.RentalDays),
            nameof(RentalItem.RentalAmount),
            nameof(RentalItem.DepositAmount)
        ];

        Assert.All(snapshotProperties, name =>
        {
            PropertyInfo property = typeof(RentalItem).GetProperty(name)!;
            Assert.False(property.SetMethod?.IsPublic ?? false, $"{name} không được có public setter.");
        });
    }

    [Fact]
    public async Task DbContext_PersistsAndReloads_ListingSnapshot()
    {
        string dbName = $"RentalDb_{Guid.NewGuid():N}";
        DbContextOptions<RentalServiceDbContext> options = new DbContextOptionsBuilder<RentalServiceDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        RentalOrder order = CreateOrder(CreateItemDto(new(2026, 10, 12), new(2026, 10, 14), quantity: 2)).Value;

        await using (RentalServiceDbContext writeContext = new(options))
        {
            writeContext.RentalOrders.Add(order);
            await writeContext.SaveChangesAsync();
        }

        await using RentalServiceDbContext readContext = new(options);
        RentalItem reloaded = await readContext.RentalItems.SingleAsync(i => i.Id == _itemId);

        Assert.Equal(CaptureSnapshot(order.Items.Single()), CaptureSnapshot(reloaded));
    }

    private static object CaptureSnapshot(RentalItem item) => new
    {
        item.EquipmentListingId,
        item.SupplierId,
        item.Quantity,
        item.ExpectedStartDate,
        item.ExpectedEndDate,
        item.ListingTitleSnapshot,
        item.EquipmentTypeId,
        item.EquipmentTypeNameSnapshot,
        item.BrandId,
        item.BrandNameSnapshot,
        item.ModelSnapshot,
        item.SpecSchemaVersionSnapshot,
        item.SpecValuesSnapshot,
        item.ThumbnailMediaIdSnapshot,
        item.DailyRateSnapshot,
        item.DepositPerUnitSnapshot,
        item.RentalDays,
        item.RentalAmount,
        item.DepositAmount
    };
}
