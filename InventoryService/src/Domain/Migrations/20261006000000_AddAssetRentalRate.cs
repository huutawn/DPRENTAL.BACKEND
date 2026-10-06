using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetRentalRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Chuẩn bị bảng staging asset_rental_rate_backfill nếu chưa có
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS inventory.asset_rental_rate_backfill (
                    asset_id uuid PRIMARY KEY,
                    daily_rate numeric NOT NULL,
                    weekly_rate numeric,
                    monthly_rate numeric,
                    currency text NOT NULL DEFAULT 'VND'
                );
                """);

            // 2. Kiểm tra tính hợp lệ của backfill:
            // - Kiểm tra sai giá trong staging (chạy kể cả khi assets rỗng) -> RAISE EXCEPTION
            // - Kiểm tra AssetId trong staging không tồn tại trong inventory.assets (chạy kể cả khi assets rỗng) -> RAISE EXCEPTION
            // - Nếu có assets hiện hữu: kiểm tra thiếu giá cho bất kỳ asset nào -> RAISE EXCEPTION
            // Tất cả thất bại atomically trong transaction của migration.
            // Nếu cả assets và staging đều rỗng: migration thành công.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    missing_count integer;
                    invalid_rate_count integer;
                    unmatched_asset_count integer;
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'inventory' AND table_name = 'asset_rental_rate_backfill') THEN
                        -- 1. Kiểm tra sai giá trong staging (chạy kể cả khi assets rỗng)
                        SELECT COUNT(*) INTO invalid_rate_count
                        FROM inventory.asset_rental_rate_backfill b
                        WHERE b.daily_rate IS NULL
                           OR trunc(b.daily_rate) <> b.daily_rate
                           OR b.daily_rate < 1
                           OR b.daily_rate > 999999999999999999
                           OR (b.weekly_rate IS NOT NULL AND (trunc(b.weekly_rate) <> b.weekly_rate OR b.weekly_rate < 1 OR b.weekly_rate > 999999999999999999))
                           OR (b.monthly_rate IS NOT NULL AND (trunc(b.monthly_rate) <> b.monthly_rate OR b.monthly_rate < 1 OR b.monthly_rate > 999999999999999999))
                           OR b.currency <> 'VND';

                        IF invalid_rate_count > 0 THEN
                            RAISE EXCEPTION 'MIGRATION_FAILED: asset_rental_rate_backfill co % ban ghi sai gia (phan le, ngoai khoang hoac sai currency).', invalid_rate_count;
                        END IF;

                        -- 2. Kiểm tra AssetId trong staging không tồn tại trong inventory.assets (chạy kể cả khi assets rỗng)
                        SELECT COUNT(*) INTO unmatched_asset_count
                        FROM inventory.asset_rental_rate_backfill b
                        LEFT JOIN inventory.assets a ON b.asset_id = a.id
                        WHERE a.id IS NULL;

                        IF unmatched_asset_count > 0 THEN
                            RAISE EXCEPTION 'MIGRATION_FAILED: asset_rental_rate_backfill co % asset_id khong ton tai trong inventory.assets.', unmatched_asset_count;
                        END IF;
                    END IF;

                    -- 3. Kiểm tra thiếu giá cho asset hiện hữu (nếu DB có assets)
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'inventory' AND table_name = 'assets') THEN
                        IF EXISTS (SELECT 1 FROM inventory.assets) THEN
                            SELECT COUNT(*) INTO missing_count
                            FROM inventory.assets a
                            LEFT JOIN inventory.asset_rental_rate_backfill b ON a.id = b.asset_id
                            WHERE b.asset_id IS NULL;

                            IF missing_count > 0 THEN
                                RAISE EXCEPTION 'MIGRATION_FAILED: asset_rental_rate_backfill thieu gia cho % thiet bi trong kho.', missing_count;
                            END IF;
                        END IF;
                    END IF;
                END $$;
                """);

            // 3. Tạo bảng inventory.asset_rental_rates
            migrationBuilder.CreateTable(
                name: "asset_rental_rates",
                schema: "inventory",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    daily_rate = table.Column<decimal>(type: "numeric(18,0)", nullable: false),
                    weekly_rate = table.Column<decimal>(type: "numeric(18,0)", nullable: true),
                    monthly_rate = table.Column<decimal>(type: "numeric(18,0)", nullable: true),
                    currency = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_rental_rates", x => x.asset_id);
                    table.ForeignKey(
                        name: "fk_asset_rental_rates_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "inventory",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.CheckConstraint("ck_asset_rental_rates_asset_id", "asset_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_asset_rental_rates_daily_rate", "daily_rate >= 1 AND daily_rate <= 999999999999999999 AND trunc(daily_rate) = daily_rate");
                    table.CheckConstraint("ck_asset_rental_rates_weekly_rate", "weekly_rate IS NULL OR (weekly_rate >= 1 AND weekly_rate <= 999999999999999999 AND trunc(weekly_rate) = weekly_rate)");
                    table.CheckConstraint("ck_asset_rental_rates_monthly_rate", "monthly_rate IS NULL OR (monthly_rate >= 1 AND monthly_rate <= 999999999999999999 AND trunc(monthly_rate) = monthly_rate)");
                    table.CheckConstraint("ck_asset_rental_rates_currency", "currency = 'VND'");
                });

            // 4. Tạo B-tree index trên daily_rate
            migrationBuilder.CreateIndex(
                name: "ix_asset_rental_rates_daily_rate",
                schema: "inventory",
                table: "asset_rental_rates",
                column: "daily_rate");

            // 5. Insert giá đã xác nhận từ staging vào inventory.asset_rental_rates
            migrationBuilder.Sql("""
                INSERT INTO inventory.asset_rental_rates (
                    asset_id,
                    daily_rate,
                    weekly_rate,
                    monthly_rate,
                    currency,
                    created_at,
                    updated_at
                )
                SELECT
                    b.asset_id,
                    b.daily_rate::numeric(18,0),
                    b.weekly_rate::numeric(18,0),
                    b.monthly_rate::numeric(18,0),
                    b.currency,
                    a.created_at,
                    a.updated_at
                FROM inventory.asset_rental_rate_backfill b
                JOIN inventory.assets a ON b.asset_id = a.id;
                """);

            // 6. Xóa staging table trong cùng transaction sau khi validation và insert thành công
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS inventory.asset_rental_rate_backfill;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("AddAssetRentalRate retains asset_rental_rates table. Down is blocked to prevent destroying rental rates; forward migration required.");
        }
    }
}
