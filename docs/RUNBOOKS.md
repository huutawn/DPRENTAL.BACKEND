# ReviewService — Sổ tay vận hành và quy trình xử lý sự cố (Operations Runbooks)

Tài liệu này hướng dẫn chi tiết quy trình xử lý vận hành, phục hồi sau sự cố, xoay vòng khóa bảo mật và giám sát chỉ số của `ReviewService`.

---

## 1. Xoay vòng khóa xác thực (JWKS Key Rotation & Recovery)

### 1.1 Cơ chế hoạt động
- `ReviewJwksConfigurationManager` định kỳ tải public keys từ `http://auth-service:8080/.well-known/jwks.json`.
- Khóa được lưu vào bộ đệm trong bộ nhớ (`KeyCache`) với thời gian làm mới tự động 15 phút.
- Trường hợp gặp `kid` chưa biết trong token, hệ thống kích hoạt yêu cầu làm mới có giới hạn tần suất (tối đa 1 lần mỗi 5 giây).
- Khi `AuthService` tạm thời gián đoạn, `ReviewService` tiếp tục sử dụng khóa đã đệm hợp lệ trong tối đa `MaxStaleMinutes` (mặc định 60 phút) trước khi fail-closed để bảo đảm an toàn.

### 1.2 Quy trình xử lý khi xoay vòng khóa bị lỗi
1. **Kiểm tra trạng thái AuthService:**
   ```bash
   curl -I http://auth-service:8080/.well-known/jwks.json
   ```
2. **Kiểm tra nhật ký ReviewService:**
   Tìm log `Failed to retrieve JWKS configuration` hoặc `IDX10501: Signature validation failed`.
3. **Cưỡng bức làm mới khóa:**
   Khởi động lại một pod ReviewService để giải phóng `KeyCache` và tải bộ khóa mới nhất:
   ```bash
   kubectl rollout restart deployment/review-service -n dprental
   ```

---

## 2. Xử lý hàng đợi Transactional Outbox bị nghẽn (Parked Outbox Recovery)

### 2.1 Trạng thái thông điệp
- **Pending:** Thông điệp mới được commit trong giao dịch nghiệp vụ, chờ worker claim.
- **Published:** Đã phát thành công lên Kafka broker và nhận broker ACK.
- **Parked:** Thất bại sau 5 lần thử với exponential backoff (1s, 2s, 4s, 8s, 16s...), chuyển sang trạng thái dừng để không chặn lô xử lý tiếp theo.

### 2.2 Câu lệnh kiểm tra số lượng Outbox bị Parked
```sql
SELECT topic, event_type, count(*), min(created_at) AS oldest_parked
FROM outbox_messages
WHERE delivery_state = 'Parked'
GROUP BY topic, event_type;
```

### 2.3 Quy trình replay Outbox có kiểm soát
Khi sự cố mạng hoặc broker đã được khắc phục, kích hoạt replay cho các bản ghi bị Parked:
```sql
UPDATE outbox_messages
SET delivery_state = 'Pending',
    attempts = 0,
    next_attempt_at = NOW(),
    lease_expires_at = NULL,
    claim_owner = NULL,
    last_error_code = NULL
WHERE delivery_state = 'Parked'
  AND created_at >= NOW() - INTERVAL '24 hours';
```

---

## 3. Khôi phục điểm đối soát Rental (Reconciliation Checkpoint Resume)

### 3.1 Cơ chế đối soát
- Sweep đối soát quét cửa sổ 30 ngày từ `RentalService` qua endpoint:
  `GET /internal/orders/review-reconciliation?cursor={cursor}&limit=100`.
- Checkpoint tiến trình lưu tại bảng `reconciliation_checkpoints`.

### 3.2 Quy trình reset Checkpoint khi cần quét lại
```sql
-- Đặt lại checkpoint để quét lại từ đầu cửa sổ 30 ngày
UPDATE reconciliation_checkpoints
SET last_item_id = NULL,
    is_completed = false,
    updated_at = NOW()
WHERE sweep_id = 'default_reconciliation_sweep';
```

---

## 4. Kiểm tra sức khỏe hệ thống (Health Checks & Monitoring)

### 4.1 Liveness Probe
- **Endpoint:** `GET /health/live`
- **Mục đích:** Xác nhận tiến trình ứng dụng đang chạy. Không phụ thuộc mạng hay database bên ngoài để tránh cascading failure.
- **Kết quả kỳ vọng:** `200 OK`.

### 4.2 Readiness Probe
- **Endpoint:** `GET /health/ready`
- **Mục đích:** Kiểm tra kết nối cơ sở dữ liệu PostgreSQL và tính sẵn sàng phục vụ API.
- **Kết quả kỳ vọng:** `200 OK`.

### 4.3 Các chỉ số cần cảnh báo (Alerting Thresholds)
| Chỉ số | Ngưỡng cảnh báo | Hành động |
|---|---|---|
| Tỷ lệ lỗi 5xx | > 1% trong 5 phút | Kiểm tra log exception và database connection pool |
| Outbox Parked Count | > 10 messages | Kiểm tra Kafka broker và chạy outbox recovery |
| DB Query p95 (Summary/Public) | > 300 ms | Kiểm tra execution plan và PostgreSQL index vacuum |
| Lỗi 401/403 bất thường | > 100 req/phút | Kiểm tra tấn công brute-force hoặc JWKS key mismatch |
