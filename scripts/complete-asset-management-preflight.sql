-- ============================================================================
-- Preflight script cho Migration: CompleteInventoryAssetManagement
-- Tạo staging table inventory.asset_model_backfill và kiểm tra dữ liệu trước khi chạy migration
-- ============================================================================

-- 1. Tạo schema nếu chưa tồn tại
CREATE SCHEMA IF NOT EXISTS inventory;

-- 2. Tạo staging table inventory.asset_model_backfill
CREATE TABLE IF NOT EXISTS inventory.asset_model_backfill (
    asset_id uuid PRIMARY KEY,
    model character varying(200) NOT NULL,
    serial_number character varying(100),
    manufacture_year integer,
    thumbnail_media_id uuid
);

COMMENT ON TABLE inventory.asset_model_backfill IS 'Bảng staging tạm thời lưu trữ thông tin Model, SerialNumber, ManufactureYear và ThumbnailMediaId cho tài sản trước khi áp dụng migration CompleteInventoryAssetManagement.';

-- 3. Truy vấn kiểm tra mức độ sẵn sàng (Preflight Verification)
-- Nếu kết quả trả về missing_or_invalid_count > 0, KHÔNG ĐƯỢC CHẠY MIGRATION.
SELECT
    COUNT(a.id) AS total_assets,
    COUNT(b.asset_id) AS backfilled_count,
    COUNT(CASE
        WHEN b.asset_id IS NULL
          OR b.model IS NULL
          OR b.model !~ '\S'
          OR length(b.model) > 200
          OR (b.serial_number IS NOT NULL AND (b.serial_number !~ '\S' OR length(b.serial_number) > 100))
          OR (b.manufacture_year IS NOT NULL AND (b.manufacture_year < 1900 OR b.manufacture_year > EXTRACT(YEAR FROM (NOW() AT TIME ZONE 'Asia/Ho_Chi_Minh'))::integer))
          OR (b.thumbnail_media_id IS NOT NULL AND b.thumbnail_media_id = '00000000-0000-0000-0000-000000000000'::uuid)
        THEN 1
    END) AS missing_or_invalid_count
FROM inventory.assets a
LEFT JOIN inventory.asset_model_backfill b ON a.id = b.asset_id;
