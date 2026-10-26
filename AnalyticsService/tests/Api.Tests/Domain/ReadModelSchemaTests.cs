using Dprental.AnalyticsService.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Dprental.AnalyticsService.Api.Tests.Domain;

/// <summary>
/// Kiểm tra mô hình bảng read model mà không cần kết nối PostgreSQL.
/// </summary>
public sealed class ReadModelSchemaTests
{
    private static readonly string[] ExpectedTables =
    [
        "rental_order_facts",
        "rental_item_facts",
        "financial_settlement_facts",
        "refund_facts",
        "supplier_payout_facts",
        "analytics_inbox_messages",
        "analytics_dead_letters"
    ];

    private static readonly string[] SensitiveNameParts =
        ["Bank", "Account", "Phone", "Address", "Content", "Payload"];

    private static readonly IModel Model = LoadModel();

    [Fact]
    public void Model_ContainsExactlyTheReadModelTables()
    {
        string[] tables = Model.GetEntityTypes()
            .Select(entity => entity.GetTableName()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTables.Order(StringComparer.Ordinal), tables);
    }

    [Fact]
    public void MoneyColumns_AreBigint()
    {
        IProperty[] moneyColumns = AllProperties()
            .Where(property => property.Name.EndsWith("Amount", StringComparison.Ordinal)
                || property.Name.EndsWith("Payable", StringComparison.Ordinal)
                || property.Name == "DepositRefundDue")
            .ToArray();

        Assert.NotEmpty(moneyColumns);
        Assert.All(moneyColumns, property => Assert.Equal("bigint", property.GetColumnType()));
    }

    [Theory]
    [InlineData("rental_order_facts", "CreatedAt")]
    [InlineData("rental_order_facts", "PaidAt")]
    [InlineData("rental_order_facts", "CompletedAt")]
    [InlineData("rental_order_facts", "Status")]
    [InlineData("rental_item_facts", "SupplierId", "PaidAt")]
    [InlineData("rental_item_facts", "EquipmentTypeId", "PaidAt")]
    [InlineData("rental_item_facts", "BrandId", "PaidAt")]
    [InlineData("rental_item_facts", "EquipmentListingId", "PaidAt")]
    [InlineData("rental_item_facts", "HandoverAt")]
    [InlineData("rental_item_facts", "CompletedAt")]
    [InlineData("rental_item_facts", "Status")]
    [InlineData("financial_settlement_facts", "CompletedAt")]
    [InlineData("financial_settlement_facts", "SupplierId", "CompletedAt")]
    [InlineData("refund_facts", "CompletedAt")]
    [InlineData("refund_facts", "RentalItemId")]
    [InlineData("supplier_payout_facts", "SupplierId", "CompletedAt")]
    [InlineData("supplier_payout_facts", "Status")]
    public void ReportingIndex_Exists(string table, params string[] columns)
    {
        IEntityType entity = FindTable(table);

        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(columns));
    }

    [Fact]
    public void DeadLetterPosition_IsUnique()
    {
        IIndex index = Assert.Single(FindTable("analytics_dead_letters").GetIndexes());

        Assert.True(index.IsUnique);
        Assert.Equal(
            ["Topic", "Partition", "Offset"],
            index.Properties.Select(property => property.Name));
    }

    [Fact]
    public void RefundFacts_UsesRefundRefAndRentalItemIdAsPrimaryKey()
    {
        IKey key = FindTable("refund_facts").FindPrimaryKey()!;

        Assert.Equal(
            ["RefundRef", "RentalItemId"],
            key.Properties.Select(property => property.Name));
    }

    [Theory]
    [InlineData("rental_order_facts")]
    [InlineData("rental_item_facts")]
    public void UpdatableFacts_UseXminConcurrencyToken(string table)
    {
        IProperty rowVersion = FindTable(table).FindProperty("RowVersion")!;

        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Equal("xmin", rowVersion.GetColumnName());
    }

    [Theory]
    [InlineData("rental_order_facts")]
    [InlineData("rental_item_facts")]
    public void Status_IsStoredAsText(string table)
    {
        IProperty status = FindTable(table).FindProperty("Status")!;

        Assert.Equal("character varying(32)", status.GetColumnType());
    }

    [Fact]
    public void NoColumn_StoresSensitiveData()
    {
        string[] columnNames = AllProperties().Select(property => property.Name).ToArray();

        foreach (string sensitivePart in SensitiveNameParts)
        {
            Assert.DoesNotContain(columnNames, name => name.Contains(sensitivePart, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<IProperty> AllProperties() =>
        Model.GetEntityTypes().SelectMany(entity => entity.GetProperties());

    private static IEntityType FindTable(string table) =>
        Model.GetEntityTypes().Single(entity => entity.GetTableName() == table);

    // Dựng mô hình với provider Npgsql để có kiểu cột PostgreSQL; không mở kết nối DB.
    private static IModel LoadModel()
    {
        DbContextOptions<AnalyticsServiceDbContext> options = new DbContextOptionsBuilder<AnalyticsServiceDbContext>()
            .UseNpgsql("Host=localhost;Database=schema_test")
            .Options;

        using AnalyticsServiceDbContext context = new(options);
        return context.GetService<IDesignTimeModel>().Model;
    }
}
