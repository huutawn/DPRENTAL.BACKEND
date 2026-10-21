using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class GeneralizeManualRefundRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_manual_refund_requirements_RentalItemId",
                table: "manual_refund_requirements");

            migrationBuilder.AlterColumn<Guid>(
                name: "SettlementId",
                table: "manual_refund_requirements",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "RentalItemId",
                table: "manual_refund_requirements",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "manual_refund_requirements",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "manual_refund_requirements",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "OperationKey",
                table: "manual_refund_requirements",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "manual_refund_items",
                columns: table => new
                {
                    RequirementId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RentalAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    DepositAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_refund_items", x => new { x.RequirementId, x.RentalItemId });
                    table.CheckConstraint("ck_refund_item_amounts", "\"RentalAmount\" >= 0 AND \"DepositAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_manual_refund_items_manual_refund_requirements_RequirementId",
                        column: x => x.RequirementId,
                        principalTable: "manual_refund_requirements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve existing deposit refunds. Evidence is never rewritten by this schema migration.
            migrationBuilder.Sql("""
                INSERT INTO manual_refund_items ("RequirementId", "RentalItemId", "RentalAmount", "DepositAmount")
                SELECT "Id", "RentalItemId", 0, "Amount" FROM manual_refund_requirements;
                UPDATE manual_refund_requirements r SET "OperationKey" = s."OperationKey"
                FROM rental_item_settlements s WHERE r."SettlementId" = s."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_requirements_ExternalTransferReference",
                table: "manual_refund_requirements",
                column: "ExternalTransferReference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM manual_refund_requirements WHERE "Kind" <> 1 OR "SettlementId" IS NULL OR "RentalItemId" IS NULL OR length("Reason") > 256)
                  THEN RAISE EXCEPTION 'Cannot downgrade after general refund requests have been recorded; restore a verified backup instead.';
                  END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "manual_refund_items");

            migrationBuilder.DropIndex(
                name: "IX_manual_refund_requirements_ExternalTransferReference",
                table: "manual_refund_requirements");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "manual_refund_requirements");

            migrationBuilder.DropColumn(
                name: "OperationKey",
                table: "manual_refund_requirements");

            migrationBuilder.AlterColumn<Guid>(
                name: "SettlementId",
                table: "manual_refund_requirements",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RentalItemId",
                table: "manual_refund_requirements",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Reason",
                table: "manual_refund_requirements",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_manual_refund_requirements_RentalItemId",
                table: "manual_refund_requirements",
                column: "RentalItemId",
                unique: true);
        }
    }
}
