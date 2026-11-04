using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class AddManualRefundTransferCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransferCode",
                table: "manual_refund_requirements",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            // Giống AddSupplierPayoutTransferCode: r."Id" buộc random() tính lại cho từng dòng.
            migrationBuilder.Sql("""
                UPDATE manual_refund_requirements r SET "TransferCode" = 'DRF ' || (
                    SELECT string_agg(substr('23456789ABCDEFGHJKLMNPQRSTUVWXYZ', 1 + floor(random() * 32)::int, 1), '')
                    FROM generate_series(1, 10)
                    WHERE r."Id" IS NOT NULL)
                WHERE "TransferCode" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "TransferCode",
                table: "manual_refund_requirements",
                type: "character varying(14)",
                maxLength: 14,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(14)",
                oldMaxLength: 14,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_manual_refund_requirements_TransferCode",
                table: "manual_refund_requirements",
                column: "TransferCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_manual_refund_requirements_TransferCode",
                table: "manual_refund_requirements");

            migrationBuilder.DropColumn(
                name: "TransferCode",
                table: "manual_refund_requirements");
        }
    }
}
