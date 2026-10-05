using Dprental.CatalogService.Common.Enums;
using Dprental.CatalogService.Domain.Entities;
using Xunit;

namespace Dprental.CatalogService.Api.Tests;

public sealed class EquipmentTypeLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static EquipmentType CreateWithPublishedSchema(int version = 1)
    {
        var eq = new EquipmentType();
        var schema = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, version, new EquipmentTypeSpecDefinition(), Now, Now);
        eq.ApplyPublishedSchema(schema, Now);
        return eq;
    }

    [Fact]
    public void NewEquipmentType_StartsAsDraft()
    {
        var equipmentType = new EquipmentType();

        Assert.Equal(EquipmentTypeStatus.Draft, equipmentType.Status);
        Assert.Null(equipmentType.PublishedAt);
        Assert.Null(equipmentType.UpdatedAt);
        Assert.Null(equipmentType.CurrentSchemaVersion);
    }

    [Fact]
    public void Publish_WithoutSchema_ThrowsInvalidOperationException()
    {
        var equipmentType = new EquipmentType();
        Assert.Null(equipmentType.CurrentSchemaVersion);

        var ex = Assert.Throws<InvalidOperationException>(() => equipmentType.Publish(Now));
        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Publish_FromDraft_WithSchema_SetsPublishedAndTimestamp()
    {
        var equipmentType = CreateWithPublishedSchema();

        equipmentType.Publish(Now);

        Assert.Equal(EquipmentTypeStatus.Published, equipmentType.Status);
        Assert.Equal(Now, equipmentType.PublishedAt);
        Assert.Equal(Now, equipmentType.UpdatedAt);
    }

    [Fact]
    public void Disable_FromPublished_SetsDisabledAndKeepsPublishedAt()
    {
        var equipmentType = CreateWithPublishedSchema();
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
        var equipmentType = CreateWithPublishedSchema();
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
        var equipmentType = CreateWithPublishedSchema();
        DateTime unspecified = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Unspecified);

        equipmentType.Publish(unspecified);
        Assert.Equal(DateTimeKind.Utc, equipmentType.PublishedAt!.Value.Kind);

        equipmentType.Disable(unspecified.AddHours(1));
        Assert.Equal(DateTimeKind.Utc, equipmentType.UpdatedAt!.Value.Kind);
    }

    [Fact]
    public void Publish_WhenAlreadyPublished_Throws()
    {
        var equipmentType = CreateWithPublishedSchema();
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
        var equipmentType = CreateWithPublishedSchema();
        equipmentType.Publish(Now);
        equipmentType.Disable(Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => equipmentType.Disable(Now.AddMinutes(2)));
    }

    // ==========================================
    // AGGREGATE OPERATIONS FOR SCHEMA VERSION
    // ==========================================

    [Fact]
    public void ApplyPublishedSchema_Valid_UpdatesCurrentVersionAndUpdatedAt()
    {
        var eq = new EquipmentType();
        var schema1 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, 1, new EquipmentTypeSpecDefinition(), Now, Now);

        eq.ApplyPublishedSchema(schema1, Now);
        Assert.Equal(1, eq.CurrentSchemaVersion);
        Assert.Equal(Now, eq.UpdatedAt);

        var schema2 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, 2, new EquipmentTypeSpecDefinition(), Now.AddHours(1), Now.AddHours(1));
        eq.ApplyPublishedSchema(schema2, Now.AddHours(1));
        Assert.Equal(2, eq.CurrentSchemaVersion);
    }

    [Fact]
    public void ApplyPublishedSchema_MismatchedEquipmentTypeId_Throws()
    {
        var eq = new EquipmentType();
        var schemaOther = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), Guid.NewGuid(), 1, new EquipmentTypeSpecDefinition(), Now, Now);

        var ex = Assert.Throws<InvalidOperationException>(() => eq.ApplyPublishedSchema(schemaOther, Now));
        Assert.Contains("không thuộc", ex.Message);
    }

    [Fact]
    public void ApplyPublishedSchema_DraftSchema_Throws()
    {
        var eq = new EquipmentType();
        var draftSchema = EquipmentTypeSpecSchema.CreateDraft(
            eq.Id, 1, new EquipmentTypeSpecDefinition(), Now);

        var ex = Assert.Throws<InvalidOperationException>(() => eq.ApplyPublishedSchema(draftSchema, Now));
        Assert.Contains("đã được công khai", ex.Message);
    }

    [Fact]
    public void ApplyPublishedSchema_SmallerOrEqualVersion_Throws()
    {
        var eq = CreateWithPublishedSchema(version: 2);
        Assert.Equal(2, eq.CurrentSchemaVersion);

        var schemaV1 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, 1, new EquipmentTypeSpecDefinition(), Now, Now);

        var ex1 = Assert.Throws<InvalidOperationException>(() => eq.ApplyPublishedSchema(schemaV1, Now));
        Assert.Contains("lớn hơn", ex1.Message);

        var schemaV2 = EquipmentTypeSpecSchema.CreatePublished(
            Guid.NewGuid(), eq.Id, 2, new EquipmentTypeSpecDefinition(), Now, Now);
        var ex2 = Assert.Throws<InvalidOperationException>(() => eq.ApplyPublishedSchema(schemaV2, Now));
        Assert.Contains("lớn hơn", ex2.Message);
    }
}
