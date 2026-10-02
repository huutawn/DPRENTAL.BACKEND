using Dprental.InventoryService.Common.Enums;
using Dprental.InventoryService.Common.Utils;
using Dprental.InventoryService.Domain.Entities;
using Dprental.InventoryService.Domain.ValueObjects;
using Xunit;

namespace Dprental.InventoryService.Api.Tests.Domain;

public sealed class AllocationContractTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly RentalPeriod Period = new(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 10));

    private static AssetAllocation Manual(Guid assetId, string? key = null) =>
        AssetAllocation.CreateManual(Guid.NewGuid(), assetId, Period, Created, "MANUAL-001", operationKey: key).Value;

    [Fact]
    public void HoldRetryAgainstManualReferenceReturnsConflict()
    {
        var allocation = Manual(Guid.NewGuid());
        var retry = allocation.ValidateExternalRetry(allocation.AssetId, Period, Created.AddHours(1));
        Assert.True(retry.IsFailure);
        Assert.Equal("EXTERNAL_REFERENCE_REUSED", retry.Error.Code);
    }

    [Fact]
    public void TransitionsCollectionCannotBeMutatedByCallers()
    {
        var allocation = Manual(Guid.NewGuid());
        Assert.Throws<NotSupportedException>(() => ((ICollection<AllocationTransition>)allocation.Transitions).Clear());
    }

    [Fact]
    public void CreationKeyCannotBeReusedForCancellation()
    {
        var allocation = Manual(Guid.NewGuid(), key: "create-key");
        var result = allocation.Cancel("Lý do hủy", Created, "operator", "create-key");

        Assert.True(result.IsFailure);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", result.Error.Code);
        Assert.Equal(AllocationStatus.Allocated, allocation.Status);
        Assert.Single(allocation.Transitions);
    }

    [Fact]
    public void RetryMustMatchOperationAndPayload()
    {
        var allocation = AssetAllocation.CreateHold(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Period,
            Created.AddHours(2),
            Created,
            "tests",
            "ref-123").Value;

        Assert.True(allocation.ExtendHold(Created.AddHours(3), Created.AddHours(1), "service", "extend-key").IsSuccess);
        var count = allocation.Transitions.Count;

        // Same operation and payload -> replay succeeds
        Assert.True(allocation.ExtendHold(Created.AddHours(3), Created.AddHours(2), "service", "extend-key").IsSuccess);
        Assert.Equal(count, allocation.Transitions.Count);

        // Different payload with same key -> conflict
        Assert.True(allocation.ExtendHold(Created.AddHours(4), Created.AddHours(2), "service", "extend-key").IsFailure);

        // Different operation with same key -> conflict
        Assert.True(allocation.Allocate(Created.AddHours(2), "service", "extend-key").IsFailure);

        Assert.Equal(count, allocation.Transitions.Count);
        Assert.Equal(Created.AddHours(3), allocation.HoldExpiresAt);
    }

    [Fact]
    public void ValidateExternalRetryMatchesPayload()
    {
        var assetId = Guid.NewGuid();
        var holdExpiresAt = Created.AddHours(2);
        var allocation = AssetAllocation.CreateHold(
            Guid.NewGuid(),
            assetId,
            Period,
            holdExpiresAt,
            Created,
            "tests",
            "ref-123",
            "Ghi chú").Value;

        Assert.True(allocation.ValidateExternalRetry(assetId, Period, holdExpiresAt, "Ghi chú").IsSuccess);
        Assert.True(allocation.ValidateExternalRetry(Guid.NewGuid(), Period, holdExpiresAt, "Ghi chú").IsFailure);
        Assert.True(allocation.ValidateExternalRetry(assetId, new RentalPeriod(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 11)), holdExpiresAt, "Ghi chú").IsFailure);
        Assert.True(allocation.ValidateExternalRetry(assetId, Period, holdExpiresAt, "Ghi chú khác").IsFailure);
    }

    [Fact]
    public void HoldExpiresAfterStartDateFails()
    {
        DateTimeOffset startOfDayUtc = BusinessDateTime.StartOfDayUtc(Period.StartDate);
        var result = AssetAllocation.CreateHold(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Period,
            startOfDayUtc.AddHours(1),
            Created,
            "tests",
            "ref-invalid");

        Assert.True(result.IsFailure);
        Assert.Equal("HOLD_EXPIRES_AFTER_START", result.Error.Code);
    }
}
