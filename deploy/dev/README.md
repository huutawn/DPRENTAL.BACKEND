# DPRENTAL development deployment

Bundle này vận hành môi trường dev trên một VPS theo topology production-like. Gateway, AuthService, PostgreSQL và Valkey chỉ giao tiếp qua Docker network nội bộ. Trên VPS Opsi, Caddy chỉ bind loopback `127.0.0.1:18080` và `127.0.0.1:18443`; Opsi tunnel proxy sở hữu public `80` và `443` rồi chuyển tiếp vào hai cổng này.

Không dùng file này cho local development. Local stack tiếp tục dùng `compose.yaml` ở repository root và tự build source.

## Điều kiện trước khi triển khai

- DNS của `DEV_DOMAIN` trỏ tới VPS; Opsi tunnel proxy phải active ở public TCP `80` và `443`.
- VPS có Docker Engine và Docker Compose plugin.
- VPS đã `docker login ghcr.io` bằng token chỉ có quyền đọc image packages.
- Các image được build/push từ máy phát hành hoặc CI với tag commit SHA. VPS chỉ `pull`, không build source.
- AuthService image có Dockerfile runtime chính thức; Auth migration image chạy migration của đúng release.
- AuthService và Gateway có `/health/live` và `/health/ready`. Gateway chỉ được khởi động sau khi AuthService readiness đạt.

## Chuẩn bị VPS

```bash
sudo install -d -m 0750 /opt/dprental/dev/secrets
sudo chown "$USER":"$USER" /opt/dprental/dev /opt/dprental/dev/secrets
cd /opt/dprental/dev
cp .env.example .env
chmod 600 .env
```

Tạo hai PEM file dưới `secrets/`; chúng không được commit. AuthService runtime chạy bằng UID `1654`, vì vậy file phải chỉ cho UID này đọc được:

```bash
sudo chown 1654:1654 secrets/auth-jwt-private.pem secrets/auth-jwt-public.pem
sudo chmod 400 secrets/auth-jwt-private.pem secrets/auth-jwt-public.pem
```

JWT issuer/audience trong `.env` phải khớp giữa Gateway và AuthService. Không tái sử dụng private key local được Compose root tự sinh.

Hiện AuthService nhận PostgreSQL connection string qua environment variable, nên `POSTGRES_PASSWORD` nằm trong `.env` trên VPS và không được commit. Đây là giới hạn tạm thời của ứng dụng hiện tại; trước khi dùng topology này cho production, thay bằng Docker secret hoặc secret manager được AuthService đọc trực tiếp. JWT private key đã dùng file secret ngay từ đầu.

## Quy trình phát hành

1. Cập nhật ba image tag cùng commit release trong `.env`: Gateway, AuthService và Auth migrator.
2. Render cấu hình để phát hiện biến hoặc secret file thiếu:

   ```bash
   docker compose config
   ```

3. Pull image, chạy migration một lần, sau đó khởi động dịch vụ:

   ```bash
   docker compose pull
   docker compose --profile migration pull auth-migrate
   docker compose up -d postgres valkey
   docker compose --profile migration run --rm auth-migrate
   docker compose up -d --remove-orphans
   ```

4. Xác nhận Gateway readiness qua domain: `https://<DEV_DOMAIN>/health/ready`, sau đó kiểm thử JWKS, register, login và refresh token qua Gateway.

Không tự động chạy rollback migration. Chỉ rollback application image khi migration tương thích ngược; schema thay đổi phá vỡ tương thích phải theo expand-and-contract.

## Thêm service mới

Mỗi service mới phải có image SHA riêng, migration job riêng, health endpoint, credentials/secret namespace riêng và database/user riêng. Thêm service vào network `services`; chỉ thêm route Gateway và public access khi service thực sự là API công khai. Không service nào được đọc hoặc ghi trực tiếp bảng database của service khác.
