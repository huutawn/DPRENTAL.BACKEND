-- ============================================================================
-- Preflight script cho Migration: RefactorInventoryBoundary
-- Tạo staging table inventory.asset_backfill và kiểm tra dữ liệu trước khi chạy migration
-- ============================================================================

-- 1. Tạo schema nếu chưa tồn tại
CREATE SCHEMA IF NOT EXISTS inventory;

-- 2. Tạo staging table inventory.asset_backfill
CREATE TABLE IF NOT EXISTS inventory.asset_backfill (
    asset_id uuid PRIMARY KEY,
    owner_user_id uuid NOT NULL,
    equipment_type_id uuid NOT NULL,
    schema_version integer NOT NULL CHECK (schema_version >= 1),
    spec_values jsonb NOT NULL
);

COMMENT ON TABLE inventory.asset_backfill IS 'Bảng staging tạm thời lưu trữ thông tin backfill OwnerUserId và EquipmentType cho tài sản trước khi áp dụng migration RefactorInventoryBoundary.';

-- 3. Truy vấn kiểm tra mức độ sẵn sàng (Preflight Verification)
-- Nếu kết quả trả về missing_or_invalid_count > 0, KHÔNG ĐƯỢC CHẠY MIGRATION.
SELECT
    COUNT(e.id) AS total_legacy_equipments,
    COUNT(b.asset_id) AS backfilled_count,
    COUNT(CASE
        WHEN b.asset_id IS NULL
          OR b.owner_user_id = '00000000-0000-0000-0000-000000000000'::uuid
          OR b.equipment_type_id = '00000000-0000-0000-0000-000000000000'::uuid
          OR b.schema_version < 1
          OR b.spec_values IS NULL
          OR jsonb_typeof(b.spec_values) <> 'object'
        THEN 1
    END) AS missing_or_invalid_count
FROM inventory.equipments e
LEFT JOIN inventory.asset_backfill b ON e.id = b.asset_id;
