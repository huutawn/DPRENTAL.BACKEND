using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace RentalService.Domain.Migrations;

[DbContext(typeof(RentalServiceDbContext))]
[Migration("20261109150000_AddClaimedDamageAndDisputedStatus")]
public sealed class AddClaimedDamageAndDisputedStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "ClaimedDamageAmount",
            schema: "rental",
            table: "deduction_proposals",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "SupplierOfferAmount", schema: "rental", table: "disputes",
            type: "numeric(18,2)", precision: 18, scale: 2, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "SupplierOfferedAt", schema: "rental", table: "disputes",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "SupplierOfferedById", schema: "rental", table: "disputes",
            type: "uuid", nullable: true);

        migrationBuilder.Sql("""
            UPDATE rental.rental_items AS item
            SET "Status" = 9
            FROM rental.deduction_proposals AS proposal
            WHERE proposal."RentalItemId" = item."Id"
              AND item."Status" = 4
              AND proposal."Status" = 3
              AND item."FinalDeductionAmount" IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SupplierOfferedById", schema: "rental", table: "disputes");
        migrationBuilder.DropColumn(name: "SupplierOfferedAt", schema: "rental", table: "disputes");
        migrationBuilder.DropColumn(name: "SupplierOfferAmount", schema: "rental", table: "disputes");
        migrationBuilder.Sql("""
            UPDATE rental.rental_items AS item
            SET "Status" = 4
            FROM rental.deduction_proposals AS proposal
            WHERE proposal."RentalItemId" = item."Id"
              AND item."Status" = 9
              AND proposal."Status" = 3
              AND item."FinalDeductionAmount" IS NULL;
            """);

        migrationBuilder.DropColumn(
            name: "ClaimedDamageAmount",
            schema: "rental",
            table: "deduction_proposals");
    }
}
