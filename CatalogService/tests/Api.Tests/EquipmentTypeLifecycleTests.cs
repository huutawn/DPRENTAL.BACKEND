using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class EquipmentTypeLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewEquipmentType_StartsAsDraft_WithUnlockedCode()
    {
        EquipmentType type = new();

        Assert.Equal(EquipmentTypeStatus.Draft, type.Status);
        Assert.Null(type.PublishedAtUtc);
        Assert.Null(type.DisabledAtUtc);
        Assert.False(type.IsCodeLocked);
        Assert.False(type.RequiresSpecSchema);
    }

    [Fact]
    public void Publish_FromDraft_SetsPublished_AndLocksCode()
    {
        EquipmentType type = new();

        type.Publish(Now);

        Assert.Equal(EquipmentTypeStatus.Published, type.Status);
        Assert.Equal(Now, type.PublishedAtUtc);
        Assert.Equal(Now, type.UpdatedAtUtc);
        Assert.True(type.IsCodeLocked);
    }

    [Fact]
    public void Disable_FromPublished_SetsDisabled_AndKeepsCodeLocked()
    {
        EquipmentType type = new();
        type.Publish(Now);
        DateTime later = Now.AddHours(1);

        type.Disable(later);

        Assert.Equal(EquipmentTypeStatus.Disabled, type.Status);
        Assert.Equal(later, type.DisabledAtUtc);
        Assert.Equal(Now, type.PublishedAtUtc);
        Assert.True(type.IsCodeLocked);
    }

    [Fact]
    public void Publish_FromDisabled_PublishesAgain_AndClearsDisabledAt()
    {
        EquipmentType type = new();
        type.Publish(Now);
        type.Disable(Now.AddHours(1));
        DateTime republishedAt = Now.AddHours(2);

        type.Publish(republishedAt);

        Assert.Equal(EquipmentTypeStatus.Published, type.Status);
        Assert.Equal(republishedAt, type.PublishedAtUtc);
        Assert.Null(type.DisabledAtUtc);
    }

    [Fact]
    public void InvalidTransitions_Throw_AndLeaveStatusUnchanged()
    {
        EquipmentType draft = new();
        Assert.Throws<InvalidOperationException>(() => draft.Disable(Now));
        Assert.Equal(EquipmentTypeStatus.Draft, draft.Status);

        EquipmentType published = new();
        published.Publish(Now);
        Assert.Throws<InvalidOperationException>(() => published.Publish(Now.AddHours(1)));

        published.Disable(Now.AddHours(1));
        Assert.Throws<InvalidOperationException>(() => published.Disable(Now.AddHours(2)));
        Assert.Equal(EquipmentTypeStatus.Disabled, published.Status);
    }

    [Fact]
    public void PublishAndDisable_NormalizeTimestampsToUtc()
    {
        EquipmentType type = new();

        type.Publish(Now.ToLocalTime());
        type.Disable(DateTime.SpecifyKind(Now.AddHours(1), DateTimeKind.Unspecified));

        Assert.Equal(DateTimeKind.Utc, type.PublishedAtUtc!.Value.Kind);
        Assert.Equal(Now, type.PublishedAtUtc);
        Assert.Equal(DateTimeKind.Utc, type.DisabledAtUtc!.Value.Kind);
        Assert.Equal(Now.AddHours(1), type.DisabledAtUtc);
    }

    [Theory]
    [InlineData("Forklift 1.5–3.5T", "FORKLIFT 1.5–3.5T")]
    [InlineData("  forklift   1.5–3.5t ", "FORKLIFT 1.5–3.5T")]
    [InlineData("Máy xúc\tđào", "MÁY XÚC ĐÀO")]
    public void Rename_TrimsName_AndNormalizesForDuplicateDetection(string input, string expectedNormalized)
    {
        EquipmentType type = new();

        type.Rename(input);

        Assert.Equal(input.Trim(), type.Name);
        Assert.Equal(expectedNormalized, type.NormalizedName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_RejectsBlankName(string input)
    {
        EquipmentType type = new();

        Assert.Throws<ArgumentException>(() => type.Rename(input));
    }
}
