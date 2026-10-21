using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment;

public partial class MergeSupplierPayoutAndE2eeModel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "SettlementId", table: "manual_refund_operation_audits", type: "uuid", nullable: true,
            oldClrType: typeof(Guid), oldType: "uuid");
        migrationBuilder.AlterColumn<Guid>(
            name: "RentalItemId", table: "manual_refund_operation_audits", type: "uuid", nullable: true,
            oldClrType: typeof(Guid), oldType: "uuid");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "RentalItemId", table: "manual_refund_operation_audits", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AlterColumn<Guid>(
            name: "SettlementId", table: "manual_refund_operation_audits", type: "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
    }
}
