using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class CompleteInventoryAssetManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Chuẩn bị bảng staging asset_model_backfill
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS inventory.asset_model_backfill (
                    asset_id uuid PRIMARY KEY,
                    model character varying(200) NOT NULL,
                    serial_number character varying(100),
                    manufacture_year integer,
                    thumbnail_media_id uuid
                );
                """);

            // 2. Kiểm tra tính hợp lệ của backfill: nếu có assets hiện hữu, tất cả phải có bản ghi model hợp lệ
            // Thiếu hoặc sai mapping sẽ RAISE EXCEPTION và rollback transaction tự động.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    missing_count integer;
                    current_vn_year integer;
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'inventory' AND table_name = 'assets') THEN
                        IF EXISTS (SELECT 1 FROM inventory.assets) THEN
                            current_vn_year := EXTRACT(YEAR FROM (NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh'))::integer;

                            SELECT COUNT(*) INTO missing_count
                            FROM inventory.assets a
                            LEFT JOIN inventory.asset_model_backfill b ON a.id = b.asset_id
                            WHERE b.asset_id IS NULL
                               OR b.model IS NULL
                               OR b.model !~ '\S'
                               OR length(b.model) > 200
                               OR (b.serial_number IS NOT NULL AND (b.serial_number !~ '\S' OR length(b.serial_number) > 100))
                               OR (b.manufacture_year IS NOT NULL AND (b.manufacture_year < 1900 OR b.manufacture_year > current_vn_year))
                               OR (b.thumbnail_media_id IS NOT NULL AND b.thumbnail_media_id = '00000000-0000-0000-0000-000000000000'::uuid);

                            IF missing_count > 0 THEN
                                RAISE EXCEPTION 'MIGRATION_FAILED: asset_model_backfill thieu hoac khong hop le cho % thiet bi trong kho.', missing_count;
                            END IF;
                        END IF;
                    END IF;
                END $$;
                """);

            // 3. Thêm các cột physical mới vào inventory.assets
            migrationBuilder.Sql("""
                ALTER TABLE inventory.assets ADD COLUMN IF NOT EXISTS model character varying(200);
                ALTER TABLE inventory.assets ADD COLUMN IF NOT EXISTS serial_number character varying(100);
                ALTER TABLE inventory.assets ADD COLUMN IF NOT EXISTS manufacture_year integer;
                ALTER TABLE inventory.assets ADD COLUMN IF NOT EXISTS thumbnail_media_id uuid;
                """);

            // 4. Backfill dữ liệu model từ asset_model_backfill (nếu có assets hiện hữu)
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM inventory.assets) THEN
                        UPDATE inventory.assets a
                        SET model = regexp_replace(regexp_replace(b.model, '^\s+', ''), '\s+$', ''),
                            serial_number = CASE
                                WHEN b.serial_number IS NOT NULL AND b.serial_number ~ '\S'
                                THEN regexp_replace(regexp_replace(b.serial_number, '^\s+', ''), '\s+$', '')
                                ELSE NULL
                            END,
                            manufacture_year = b.manufacture_year,
                            thumbnail_media_id = b.thumbnail_media_id
                        FROM inventory.asset_model_backfill b
                        WHERE a.id = b.asset_id;
                    END IF;
                END $$;
                """);

            // 5. Cấu hình cột model thành NOT NULL
            migrationBuilder.Sql("""
                ALTER TABLE inventory.assets ALTER COLUMN model SET NOT NULL;
                """);

            // 6. Xóa staging table asset_model_backfill
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS inventory.asset_model_backfill;
                """);

            // 7. Bỏ organization_id và index của nó khỏi inventory.assets
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS inventory.ix_assets_organization_id;
                ALTER TABLE inventory.assets DROP COLUMN IF EXISTS organization_id;
                """);

            // 8. Cập nhật check constraint ck_assets_identity
            migrationBuilder.Sql("""
                ALTER TABLE inventory.assets DROP CONSTRAINT IF EXISTS ck_assets_identity;
                ALTER TABLE inventory.assets ADD CONSTRAINT ck_assets_identity CHECK (
                    id <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid
                    AND equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid
                );
                """);

            // 9. Thêm check constraints cho các cột physical mới
            migrationBuilder.Sql("""
                ALTER TABLE inventory.assets ADD CONSTRAINT ck_assets_model CHECK (
                    model ~ '\S' AND length(model) <= 200
                );
                ALTER TABLE inventory.assets ADD CONSTRAINT ck_assets_serial_number CHECK (
                    serial_number IS NULL OR (serial_number ~ '\S' AND length(serial_number) <= 100)
                );
                ALTER TABLE inventory.assets ADD CONSTRAINT ck_assets_manufacture_year CHECK (
                    manufacture_year IS NULL OR manufacture_year >= 1900
                );
                ALTER TABLE inventory.assets ADD CONSTRAINT ck_assets_thumbnail_media_id CHECK (
                    thumbnail_media_id IS NULL OR thumbnail_media_id <> '00000000-0000-0000-0000-000000000000'::uuid
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("CompleteInventoryAssetManagement removes organization_id. Restore a database backup to roll back this migration.");
        }
    }
}
