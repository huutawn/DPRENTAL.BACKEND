using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class AddSupplierPayoutTransferCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TransferCode",
                table: "supplier_payouts",
                type: "character varying(14)",
                maxLength: 14,
                nullable: true);

            // Payout cũ cần mã riêng trước khi bật unique index. p."Id" trong subquery buộc PostgreSQL
            // tính lại random() cho từng dòng; thiếu nó mọi dòng nhận cùng một mã.
            migrationBuilder.Sql("""
                UPDATE supplier_payouts p SET "TransferCode" = 'DPO ' || (
                    SELECT string_agg(substr('23456789ABCDEFGHJKLMNPQRSTUVWXYZ', 1 + floor(random() * 32)::int, 1), '')
                    FROM generate_series(1, 10)
                    WHERE p."Id" IS NOT NULL)
                WHERE "TransferCode" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "TransferCode",
                table: "supplier_payouts",
                type: "character varying(14)",
                maxLength: 14,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(14)",
                oldMaxLength: 14,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_supplier_payouts_TransferCode",
                table: "supplier_payouts",
                column: "TransferCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_supplier_payouts_TransferCode",
                table: "supplier_payouts");

            migrationBuilder.DropColumn(
                name: "TransferCode",
                table: "supplier_payouts");
        }
    }
}
