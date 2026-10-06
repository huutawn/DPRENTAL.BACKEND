-- ============================================================================
-- Preflight script cho Migration: AddAssetRentalRate
-- Tạo staging table inventory.asset_rental_rate_backfill và kiểm tra dữ liệu trước khi chạy migration
-- ============================================================================

-- 1. Tạo schema nếu chưa tồn tại
CREATE SCHEMA IF NOT EXISTS inventory;

-- 2. Tạo staging table inventory.asset_rental_rate_backfill với numeric không giới hạn precision
CREATE TABLE IF NOT EXISTS inventory.asset_rental_rate_backfill (
    asset_id uuid PRIMARY KEY,
    daily_rate numeric NOT NULL,
    weekly_rate numeric,
    monthly_rate numeric,
    currency text NOT NULL DEFAULT 'VND'
);

COMMENT ON TABLE inventory.asset_rental_rate_backfill IS 'Bảng staging tạm thời lưu trữ thông tin giá thuê niêm yết (DailyRate, WeeklyRate, MonthlyRate, Currency) cho tài sản trước khi áp dụng migration AddAssetRentalRate.';

-- 3. Truy vấn kiểm tra mức độ sẵn sàng (Preflight Verification)
-- Nếu kết quả trả về missing_count > 0, invalid_rate_count > 0 hoặc unmatched_asset_count > 0, KHÔNG ĐƯỢC CHẠY MIGRATION.
SELECT
    (SELECT COUNT(*) FROM inventory.assets) AS total_assets,
    (SELECT COUNT(*) FROM inventory.asset_rental_rate_backfill) AS backfilled_count,
    (SELECT COUNT(*) FROM inventory.assets a LEFT JOIN inventory.asset_rental_rate_backfill b ON a.id = b.asset_id WHERE b.asset_id IS NULL) AS missing_count,
    (SELECT COUNT(*) FROM inventory.asset_rental_rate_backfill b
     WHERE b.daily_rate IS NULL
        OR trunc(b.daily_rate) <> b.daily_rate
        OR b.daily_rate < 1
        OR b.daily_rate > 999999999999999999
        OR (b.weekly_rate IS NOT NULL AND (trunc(b.weekly_rate) <> b.weekly_rate OR b.weekly_rate < 1 OR b.weekly_rate > 999999999999999999))
        OR (b.monthly_rate IS NOT NULL AND (trunc(b.monthly_rate) <> b.monthly_rate OR b.monthly_rate < 1 OR b.monthly_rate > 999999999999999999))
        OR b.currency <> 'VND') AS invalid_rate_count,
    (SELECT COUNT(*) FROM inventory.asset_rental_rate_backfill st LEFT JOIN inventory.assets ast ON st.asset_id = ast.id WHERE ast.id IS NULL) AS unmatched_asset_count;
