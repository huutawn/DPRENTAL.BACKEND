using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderCodeToPaymentTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payment_transactions) THEN
                        RAISE EXCEPTION 'Cannot add ProviderCode: payment_transactions table must be empty before migration.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<string>(
                name: "ProviderCode",
                table: "payment_transactions",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM payment_transactions) THEN
                        RAISE EXCEPTION 'Cannot drop ProviderCode: payment_transactions table has records; forward-fix required to prevent data loss.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "ProviderCode",
                table: "payment_transactions");
        }
    }
}
