# Dọn dữ liệu và phục hồi ứng dụng DPRENTAL trên VPS dev

Script dùng cho server dev `dprental-dev` hoặc project thử nghiệm `dprental-e2e-*`, thư mục triển khai `/opt/dprental/dev` (hoặc cấu hình qua `DPR_DEPLOY_DIRECTORY`).

## 1. Cách dùng

```bash
# Xem trước kế hoạch (chỉ đọc, không sửa đổi dữ liệu)
bash /opt/dprental/dev/scripts/reset-data.sh --preview

# Thực thi dọn dữ liệu (cần terminal tương tác và nhập RESET <project>)
bash /opt/dprental/dev/scripts/reset-data.sh --execute

# Khôi phục dữ liệu từ bản sao lưu
bash /opt/dprental/dev/scripts/reset-data.sh --restore /opt/dprental/dev/backups/reset-YYYYMMDDTHHMMSSZ-XXXXXX

# Chạy trên project thử nghiệm E2E
bash /opt/dprental/dev/scripts/reset-data.sh --project dprental-e2e-run1 --preview
```

## 2. Quy tắc an toàn & Khóa triển khai

- Reset, seed và deployment cùng chia sẻ khóa độc quyền `.deployment.lock` tại thư mục triển khai. Khi `--execute` hoặc `--restore` chạy, khóa được giữ từ trước khi resolve cấu hình đến sau khi toàn bộ stack khởi động lại và vượt qua kiểm tra readiness.
- Tên project mặc định là `dprental-dev`. Chỉ cho phép thêm project thử nghiệm có tiền tố `dprental-e2e-`; mọi project tùy ý khác đều bị từ chối ở bước preflight.
- Preflight kiểm tra:
  - Docker Compose exit status và cú pháp cấu hình.
  - Phân loại toàn bộ 8 named volumes: 6 volume dữ liệu được dọn (`postgres-data`, `integrity-postgres-data`, `media-minio-data`, `valkey-data`, `kafka-data`, `meilisearch-data`), 2 volume TLS/cấu hình được giữ nguyên (`caddy-data`, `caddy-config`).
  - Toàn bộ bind mount được kiểm tra: chỉ chấp nhận regular file tin cậy ở chế độ read-only (`Caddyfile`, `firebase-service-account.json`, `kafka-log4j2.yaml`).
  - Toàn bộ secrets được phân loại và kiểm tra quyền đọc.
  - Sắp xếp topo thứ tự thực thi 10 migration services theo dependency graph (`depends_on`).
  - Kiểm tra dung lượng đĩa khả dụng (tối thiểu 2000 MB) trước khi dừng stack.
  - Image inspect trên PostgreSQL và các migrators.

## 3. Trình tự thực thi (--execute)

1. Lấy khóa độc quyền `.deployment.lock`.
2. Dừng các service writer (`auth`, `media`, `catalog`, `inventory`, `rental`, `payment`, `analytics`, `recommendation`, `frontend`) để chặn ghi ngoài, giữ Meilisearch và storage còn hoạt động.
3. Xuất cấu hình Meilisearch API keys vào bản sao lưu (`meilisearch-key-config.json`).
4. Dừng toàn bộ stack và kiểm tra không còn container nào mount vào 6 volume dữ liệu.
5. Sao lưu đồng bộ 6 data volume dưới dạng archive `tar.gz` kèm SHA256SUMS.
6. Sao lưu cấu hình (`compose.yaml`, `.env`, `release.env`, `mounted-config/`, `secrets/`) và chụp manifest metadata digest.
7. Xóa 6 volume dữ liệu bằng `docker volume rm` (không dùng `down -v` hoặc `prune`).
8. Khởi động storage (`postgres`, `integrity-postgres`, `valkey`, `kafka`, `media-minio`, `meilisearch`).
9. Khôi phục Meilisearch API keys từ cấu hình đã sao lưu.
10. Khởi chạy `media-minio-init` để khởi tạo MinIO buckets/policies.
11. Thực thi 10 migration services theo đúng thứ tự phụ thuộc.
12. Khởi động toàn bộ ứng dụng và kiểm tra trạng thái hoạt động (`docker compose ps`).

## 4. Khôi phục từ bản sao lưu (--restore)

Khi xảy ra lỗi hoặc cần rollback dữ liệu về mốc snapshot:
1. Lấy khóa `.deployment.lock`.
2. Kiểm tra tính toàn vẹn của thư mục backup qua `sha256sum --check SHA256SUMS`.
3. Dừng stack và khôi phục 6 volume từ các file `.tar.gz` tương ứng.
4. Khôi phục file cấu hình mount và Meilisearch keys.
5. Khởi động storage và các service ứng dụng, kiểm tra readiness.
