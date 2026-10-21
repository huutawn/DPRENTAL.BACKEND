using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.Domain.Migrations.Payment
{
    /// <inheritdoc />
    public partial class AddIntegritySnapshotVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_name = 'invoices' AND column_name = 'IntegritySnapshotVersion'
                    ) THEN
                        ALTER TABLE invoices
                        ADD COLUMN "IntegritySnapshotVersion" integer NOT NULL DEFAULT 1;
                    ELSE
                        -- Ensure proper type, nullability, and default if column was pre-created
                        ALTER TABLE invoices
                        ALTER COLUMN "IntegritySnapshotVersion" SET DATA TYPE integer,
                        ALTER COLUMN "IntegritySnapshotVersion" SET NOT NULL,
                        ALTER COLUMN "IntegritySnapshotVersion" SET DEFAULT 1;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_name = 'invoices' AND column_name = 'IntegritySnapshotVersion'
                    ) THEN
                        ALTER TABLE invoices
                        DROP COLUMN "IntegritySnapshotVersion";
                    END IF;
                END $$;
                """);
        }
    }
}
