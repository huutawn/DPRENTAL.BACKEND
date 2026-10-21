using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dprental.PaymentService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransactionTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PaymentRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Gateway = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubAccount = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TransferType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TransferAmount = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    Accumulated = table.Column<decimal>(type: "numeric(18,0)", nullable: true),
                    TransactionDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReferenceCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Content = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PaymentCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvoiceRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_transactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_InvoiceRef",
                table: "payment_transactions",
                column: "InvoiceRef");

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_PaymentRef",
                table: "payment_transactions",
                column: "PaymentRef");

            migrationBuilder.CreateIndex(
                name: "IX_payment_transactions_Provider_ProviderTransactionId",
                table: "payment_transactions",
                columns: new[] { "Provider", "ProviderTransactionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_transactions");
        }
    }
}
