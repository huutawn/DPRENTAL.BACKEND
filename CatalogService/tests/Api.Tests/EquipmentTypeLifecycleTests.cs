using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class EquipmentTypeLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewEquipmentType_StartsAsDraft()
    {
        var equipmentType = new EquipmentType();

        Assert.Equal(EquipmentTypeStatus.Draft, equipmentType.Status);
        Assert.Null(equipmentType.PublishedAt);
        Assert.Null(equipmentType.UpdatedAt);
    }

    [Fact]
    public void Publish_FromDraft_SetsPublishedAndTimestamp()
    {
        var equipmentType = new EquipmentType();

        equipmentType.Publish(Now);

        Assert.Equal(EquipmentTypeStatus.Published, equipmentType.Status);
        Assert.Equal(Now, equipmentType.PublishedAt);
        Assert.Equal(Now, equipmentType.UpdatedAt);
    }

    [Fact]
    public void Disable_FromPublished_SetsDisabledAndKeepsPublishedAt()
    {
        var equipmentType = new EquipmentType();
        equipmentType.Publish(Now);

        DateTime later = Now.AddHours(2);
        equipmentType.Disable(later);

        Assert.Equal(EquipmentTypeStatus.Disabled, equipmentType.Status);
        Assert.Equal(Now, equipmentType.PublishedAt);
        Assert.Equal(later, equipmentType.UpdatedAt);
    }

    [Fact]
    public void Publish_FromDisabled_PublishesAgainWithNewTimestamp()
    {
        var equipmentType = new EquipmentType();
        equipmentType.Publish(Now);
        equipmentType.Disable(Now.AddHours(1));

        DateTime republishedAt = Now.AddDays(1);
        equipmentType.Publish(republishedAt);

        Assert.Equal(EquipmentTypeStatus.Published, equipmentType.Status);
        Assert.Equal(republishedAt, equipmentType.PublishedAt);
    }

    [Fact]
    public void PublishAndDisable_NormalizeTimestampsToUtc()
    {
        var equipmentType = new EquipmentType();
        DateTime unspecified = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Unspecified);

        equipmentType.Publish(unspecified);
        Assert.Equal(DateTimeKind.Utc, equipmentType.PublishedAt!.Value.Kind);

        equipmentType.Disable(unspecified.AddHours(1));
        Assert.Equal(DateTimeKind.Utc, equipmentType.UpdatedAt!.Value.Kind);
    }

    [Fact]
    public void Publish_WhenAlreadyPublished_Throws()
    {
        var equipmentType = new EquipmentType();
        equipmentType.Publish(Now);

        Assert.Throws<InvalidOperationException>(() => equipmentType.Publish(Now.AddMinutes(5)));
    }

    [Fact]
    public void Disable_FromDraft_Throws()
    {
        var equipmentType = new EquipmentType();

        Assert.Throws<InvalidOperationException>(() => equipmentType.Disable(Now));
        Assert.Equal(EquipmentTypeStatus.Draft, equipmentType.Status);
    }

    [Fact]
    public void Disable_WhenAlreadyDisabled_Throws()
    {
        var equipmentType = new EquipmentType();
        equipmentType.Publish(Now);
        equipmentType.Disable(Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => equipmentType.Disable(Now.AddMinutes(2)));
    }
}
