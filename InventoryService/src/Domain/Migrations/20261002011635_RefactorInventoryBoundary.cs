using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RefactorInventoryBoundary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Drop bảng phụ thuộc không còn thuộc Inventory boundary:
            // Drop Notification trước, tiếp theo SagaInstance, sau đó DamageReport
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS inventory.notifications CASCADE;
                DROP TABLE IF EXISTS inventory.saga_instances CASCADE;
                DROP TABLE IF EXISTS inventory.damage_reports CASCADE;
                """);

            // 2. Chuẩn bị bảng staging asset_backfill (nếu chưa tạo bởi preflight script)
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS inventory.asset_backfill (
                    asset_id uuid PRIMARY KEY,
                    owner_user_id uuid NOT NULL,
                    equipment_type_id uuid NOT NULL,
                    schema_version integer NOT NULL CHECK (schema_version >= 1),
                    spec_values jsonb NOT NULL
                );
                """);

            // 3. Kiểm tra tính hợp lệ của backfill: nếu có equipments hiện hữu, tất cả phải có bản ghi backfill hợp lệ
            // Thiếu hoặc sai mapping sẽ RAISE EXCEPTION và rollback transaction tự động.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    missing_count integer;
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'inventory' AND table_name = 'equipments') THEN
                        IF EXISTS (SELECT 1 FROM inventory.equipments) THEN
                            SELECT COUNT(*) INTO missing_count
                            FROM inventory.equipments e
                            LEFT JOIN inventory.asset_backfill b ON e.id = b.asset_id
                            WHERE b.asset_id IS NULL
                               OR b.owner_user_id = '00000000-0000-0000-0000-000000000000'::uuid
                               OR b.equipment_type_id = '00000000-0000-0000-0000-000000000000'::uuid
                               OR b.schema_version < 1
                               OR b.spec_values IS NULL;

                            IF missing_count > 0 THEN
                                RAISE EXCEPTION 'MIGRATION_FAILED: asset_backfill thieu hoac khong hop le cho % thiet bi trong kho.', missing_count;
                            END IF;
                        END IF;
                    END IF;
                END $$;
                """);

            // 4. Refactor bảng equipments -> assets:
            // Giữ nguyên id, organization_id, created_at, updated_at, xmin;
            // Thêm owner_user_id, equipment_type_id, schema_version, spec_values;
            // Xóa catalog_lifecycle_status;
            // Tính toán initial accepting_bookings = (old accepting_bookings AND old catalog_lifecycle_status == 1 (Active)).
            migrationBuilder.Sql("""
                ALTER TABLE inventory.equipments RENAME TO assets;
                ALTER TABLE inventory.assets RENAME CONSTRAINT "PK_equipments" TO "PK_assets";

                ALTER TABLE inventory.assets ADD COLUMN owner_user_id uuid;
                ALTER TABLE inventory.assets ADD COLUMN equipment_type_id uuid;
                ALTER TABLE inventory.assets ADD COLUMN schema_version integer;
                ALTER TABLE inventory.assets ADD COLUMN spec_values jsonb;

                UPDATE inventory.assets a
                SET owner_user_id = b.owner_user_id,
                    equipment_type_id = b.equipment_type_id,
                    schema_version = b.schema_version,
                    spec_values = b.spec_values,
                    accepting_bookings = (a.accepting_bookings AND a.catalog_lifecycle_status = 1)
                FROM inventory.asset_backfill b
                WHERE a.id = b.asset_id;

                ALTER TABLE inventory.assets ALTER COLUMN owner_user_id SET NOT NULL;
                ALTER TABLE inventory.assets ALTER COLUMN equipment_type_id SET NOT NULL;
                ALTER TABLE inventory.assets ALTER COLUMN schema_version SET NOT NULL;
                ALTER TABLE inventory.assets ALTER COLUMN spec_values SET NOT NULL;

                ALTER TABLE inventory.assets DROP COLUMN catalog_lifecycle_status;

                DROP INDEX IF EXISTS inventory.ix_equipments_organization_id;
                CREATE INDEX ix_assets_owner_user_id ON inventory.assets (owner_user_id);
                CREATE INDEX ix_assets_organization_id ON inventory.assets (organization_id);
                CREATE INDEX ix_assets_equipment_type_id ON inventory.assets (equipment_type_id);
                """);

            // 5. Xóa staging table asset_backfill sau khi chuyển đổi thành công
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS inventory.asset_backfill;
                """);

            // 6. Refactor bảng calendar_blocks:
            // Đổi equipment_id -> asset_id, đổi FK và Index tương ứng
            migrationBuilder.Sql("""
                ALTER TABLE inventory.calendar_blocks DROP CONSTRAINT IF EXISTS "FK_calendar_blocks_equipments_equipment_id";
                ALTER TABLE inventory.calendar_blocks RENAME COLUMN equipment_id TO asset_id;
                ALTER INDEX IF EXISTS inventory.ix_calendar_blocks_equipment_dates RENAME TO ix_calendar_blocks_asset_dates;
                ALTER TABLE inventory.calendar_blocks ADD CONSTRAINT "FK_calendar_blocks_assets_asset_id"
                    FOREIGN KEY (asset_id) REFERENCES inventory.assets (id) ON DELETE RESTRICT;
                """);

            // 7. Refactor bảng reservations -> asset_allocations:
            // Drop sequence, drop old constraints và indexes;
            // Map status: Confirmed(2) -> Allocated(2), InUse(3) -> Allocated(2), Completed(4) -> Released(6);
            // Chuẩn hóa source_system và external_reference cho manual rows;
            // Drop các cột PII, thương mại, biên bản bàn giao, minh chứng ảnh;
            // Đổi tên bảng và FK.
            migrationBuilder.Sql("""
                DROP SEQUENCE IF EXISTS inventory."ReservationNumberSequence" CASCADE;

                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "FK_reservations_equipments_equipment_id";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_rental_period_order";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_rental_period_duration";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_held_hold_expires_at";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_delivery_info";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_return_info";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_external_metadata";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_source_status";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_status";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_return_condition";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_return_time";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_damaged_evidence";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_delivery_photos_count";
                ALTER TABLE inventory.reservations DROP CONSTRAINT IF EXISTS "ck_reservations_return_photos_count";

                DROP INDEX IF EXISTS inventory.ix_reservations_reservation_number;
                DROP INDEX IF EXISTS inventory.ix_reservations_source_system_external_reference;
                DROP INDEX IF EXISTS inventory.ix_reservations_hold_expiry;
                DROP INDEX IF EXISTS inventory.ix_reservations_renter_status;
                DROP INDEX IF EXISTS inventory.ix_reservations_equipment_status;

                -- Mapping status: Held(1)->1, Confirmed(2)->2, InUse(3)->2, Completed(4)->6, Cancelled(5)->5, Released(6)->6
                UPDATE inventory.reservations
                SET status = CASE
                    WHEN status = 3 THEN 2
                    WHEN status = 4 THEN 6
                    ELSE status
                END;

                -- Manual rows dùng source_system='inventory.manual' và reference từ allocation id
                UPDATE inventory.reservations
                SET source_system = 'inventory.manual',
                    external_reference = id::text
                WHERE source_type = 1 OR source_system IS NULL OR length(btrim(source_system)) = 0 OR external_reference IS NULL OR length(btrim(external_reference)) = 0;

                ALTER TABLE inventory.reservations RENAME TO asset_allocations;
                ALTER TABLE inventory.asset_allocations RENAME CONSTRAINT "PK_reservations" TO "PK_asset_allocations";
                ALTER TABLE inventory.asset_allocations RENAME COLUMN equipment_id TO asset_id;

                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS reservation_number;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS source_type;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS renter_account_id;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS manual_renter_name;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS manual_renter_phone;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS delivered_at;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS delivered_by;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS delivery_condition;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS delivery_photo_ids;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS returned_at;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS returned_by;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS return_condition;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS return_condition_notes;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS return_photo_ids;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS cancelled_at;
                ALTER TABLE inventory.asset_allocations DROP COLUMN IF EXISTS cancellation_reason;

                ALTER TABLE inventory.asset_allocations ALTER COLUMN source_system SET NOT NULL;
                ALTER TABLE inventory.asset_allocations ALTER COLUMN external_reference SET NOT NULL;

                ALTER TABLE inventory.asset_allocations ADD CONSTRAINT "FK_asset_allocations_assets_asset_id"
                    FOREIGN KEY (asset_id) REFERENCES inventory.assets (id) ON DELETE RESTRICT;

                CREATE UNIQUE INDEX ix_asset_allocations_source_system_external_reference
                    ON inventory.asset_allocations (source_system, external_reference);
                CREATE INDEX ix_asset_allocations_hold_expiry
                    ON inventory.asset_allocations (status, hold_expires_at)
                    WHERE "status" = 1 AND "hold_expires_at" IS NOT NULL;
                CREATE INDEX ix_asset_allocations_asset_status
                    ON inventory.asset_allocations (asset_id, status);

                ALTER TABLE inventory.asset_allocations ADD CONSTRAINT ck_asset_allocations_rental_period_order
                    CHECK ("start_date" <= "end_date");
                ALTER TABLE inventory.asset_allocations ADD CONSTRAINT ck_asset_allocations_held_hold_expires_at
                    CHECK ("status" <> 1 OR "hold_expires_at" IS NOT NULL);
                ALTER TABLE inventory.asset_allocations ADD CONSTRAINT ck_asset_allocations_status
                    CHECK ("status" IN (1, 2, 5, 6));
                ALTER TABLE inventory.asset_allocations ADD CONSTRAINT ck_asset_allocations_source_reference
                    CHECK (length(btrim("source_system")) > 0 AND length(btrim("external_reference")) > 0);
                """);

            // 8. Refactor bảng reservation_transitions -> allocation_transitions:
            // Drop old constraints và indexes;
            // Xóa Deliver audit (7), chuyển Return audit (8) thành Release (7) với reason 'Hoàn tất kỳ phân bổ';
            // Cập nhật status mapping;
            // Đổi tên bảng và FK.
            migrationBuilder.Sql("""
                ALTER TABLE inventory.reservation_transitions DROP CONSTRAINT IF EXISTS "FK_reservation_transitions_reservations_reservation_id";

                DROP INDEX IF EXISTS inventory.ix_reservation_transitions_reservation_sequence;
                DROP INDEX IF EXISTS inventory.ix_reservation_transitions_reservation_operation_key;
                DROP INDEX IF EXISTS inventory.ix_reservation_transitions_timeline;

                DELETE FROM inventory.reservation_transitions WHERE operation_type = 7;
                UPDATE inventory.reservation_transitions
                SET operation_type = 7,
                    to_status = 6,
                    reason = 'Hoàn tất kỳ phân bổ'
                WHERE operation_type = 8;

                UPDATE inventory.reservation_transitions SET from_status = 2 WHERE from_status = 3;
                UPDATE inventory.reservation_transitions SET from_status = 6 WHERE from_status = 4;
                UPDATE inventory.reservation_transitions SET to_status = 2 WHERE to_status = 3;
                UPDATE inventory.reservation_transitions SET to_status = 6 WHERE to_status = 4;

                ALTER TABLE inventory.reservation_transitions RENAME TO allocation_transitions;
                ALTER TABLE inventory.allocation_transitions RENAME CONSTRAINT "PK_reservation_transitions" TO "PK_allocation_transitions";
                ALTER TABLE inventory.allocation_transitions RENAME COLUMN reservation_id TO allocation_id;

                ALTER TABLE inventory.allocation_transitions ADD CONSTRAINT "FK_allocation_transitions_asset_allocations_allocation_id"
                    FOREIGN KEY (allocation_id) REFERENCES inventory.asset_allocations (id) ON DELETE RESTRICT;

                CREATE UNIQUE INDEX ix_allocation_transitions_allocation_sequence
                    ON inventory.allocation_transitions (allocation_id, sequence);
                CREATE UNIQUE INDEX ix_allocation_transitions_allocation_operation_key
                    ON inventory.allocation_transitions (allocation_id, operation_key)
                    WHERE "operation_key" IS NOT NULL;
                CREATE INDEX ix_allocation_transitions_timeline
                    ON inventory.allocation_transitions (allocation_id, created_at);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Migration RefactorInventoryBoundary là thao tác destructive không hỗ trợ Down tự động. Phục hồi yêu cầu sử dụng database backup và binary phiên bản trước.");
        }
    }
}
