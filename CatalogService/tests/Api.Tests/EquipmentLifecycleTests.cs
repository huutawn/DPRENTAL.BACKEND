using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class EquipmentLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewEquipment_StartsAsDraft()
    {
        Equipment equipment = new();

        Assert.Equal(EquipmentStatus.Draft, equipment.Status);
        Assert.Null(equipment.PublishedAtUtc);
        Assert.Equal(Equipment.EmptySpecifications, equipment.Specifications);
    }

    [Fact]
    public void Publish_FromDraft_SetsPublishedAndTimestamp()
    {
        Equipment equipment = new();

        equipment.Publish(Now);

        Assert.Equal(EquipmentStatus.Published, equipment.Status);
        Assert.Equal(Now, equipment.PublishedAtUtc);
        Assert.Equal(Now, equipment.UpdatedAtUtc);
    }

    [Fact]
    public void Hide_FromPublished_SetsHiddenAndKeepsPublishedAt()
    {
        Equipment equipment = new();
        equipment.Publish(Now);
        DateTimeOffset later = Now.AddHours(1);

        equipment.Hide(later);

        Assert.Equal(EquipmentStatus.Hidden, equipment.Status);
        Assert.Equal(Now, equipment.PublishedAtUtc);
        Assert.Equal(later, equipment.UpdatedAtUtc);
    }

    [Fact]
    public void Publish_FromHidden_PublishesAgainWithNewTimestamp()
    {
        Equipment equipment = new();
        equipment.Publish(Now);
        equipment.Hide(Now.AddHours(1));
        DateTimeOffset republishedAt = Now.AddHours(2);

        equipment.Publish(republishedAt);

        Assert.Equal(EquipmentStatus.Published, equipment.Status);
        Assert.Equal(republishedAt, equipment.PublishedAtUtc);
    }

    [Fact]
    public void PublishAndHide_NormalizeTimestampsToUtc()
    {
        Equipment equipment = new();
        DateTimeOffset local = new(2026, 10, 1, 15, 0, 0, TimeSpan.FromHours(7));

        equipment.Publish(local);
        equipment.Hide(local.AddHours(1));

        Assert.Equal(TimeSpan.Zero, equipment.PublishedAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, equipment.UpdatedAtUtc!.Value.Offset);
        Assert.Equal(local, equipment.PublishedAtUtc);
    }

    [Fact]
    public void Publish_WhenAlreadyPublished_Throws()
    {
        Equipment equipment = new();
        equipment.Publish(Now);

        Assert.Throws<InvalidOperationException>(() => equipment.Publish(Now.AddHours(1)));
    }

    [Fact]
    public void Hide_FromDraft_Throws()
    {
        Equipment equipment = new();

        Assert.Throws<InvalidOperationException>(() => equipment.Hide(Now));
        Assert.Equal(EquipmentStatus.Draft, equipment.Status);
    }

    [Fact]
    public void Hide_WhenAlreadyHidden_Throws()
    {
        Equipment equipment = new();
        equipment.Publish(Now);
        equipment.Hide(Now.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => equipment.Hide(Now.AddHours(2)));
    }
}
