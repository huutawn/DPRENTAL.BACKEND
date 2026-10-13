using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRefUniqueAndBackfillOutboxCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE
    dup_record RECORD;
    dup_details TEXT := '';
BEGIN
    FOR dup_record IN
        SELECT ""PaymentRef"", string_agg(""InvoiceRef"", ', ' ORDER BY ""InvoiceRef"") AS invoice_refs
        FROM invoices
        WHERE ""PaymentRef"" IS NOT NULL
        GROUP BY ""PaymentRef""
        HAVING COUNT(*) > 1
    LOOP
        dup_details := dup_details || format('[PaymentRef=%s: Invoices=%s] ', dup_record.""PaymentRef"", dup_record.invoice_refs);
    END LOOP;

    IF dup_details <> '' THEN
        RAISE EXCEPTION 'Phát hiện PaymentRef bị trùng lặp cần đối soát thủ công trước khi tạo unique index: %', dup_details;
    END IF;
END $$;");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_PaymentRef",
                table: "invoices",
                column: "PaymentRef",
                unique: true);

            migrationBuilder.Sql(@"
UPDATE outbox_messages
SET ""Payload"" = jsonb_set(""Payload""::jsonb, '{correlationId}', to_jsonb(""MessageId""::text), true)::text
WHERE ""PublishedAt"" IS NULL
  AND NULLIF(""Payload""::jsonb ->> 'correlationId', '') IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invoices_PaymentRef",
                table: "invoices");
        }
    }
}
