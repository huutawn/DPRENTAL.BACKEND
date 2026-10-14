# Dọn dữ liệu ứng dụng DPRENTAL trên VPS dev

Script dùng cho server `103.252.137.163`, thư mục triển khai `/opt/dprental/dev`. Có thể chỉ định thư mục bằng `DPR_DEPLOY_DIRECTORY`, nhưng project Compose và nhãn volume vẫn phải là `dprental-dev`.

```bash
ssh -i /home/tawn/.ssh/vps-only -p 22 tawn@103.252.137.163
bash /opt/dprental/dev/scripts/reset-data.sh --preview
bash /opt/dprental/dev/scripts/reset-data.sh --execute
```

Mặc định là `--preview`, chỉ đọc và in kế hoạch. `--execute` cần terminal tương tác và phải nhập chính xác `RESET dprental-dev`. Tạm dừng triển khai trong thời gian thực hiện; ứng dụng sẽ có thời gian gián đoạn.

Phạm vi gồm toàn bộ tài khoản, dữ liệu nghiệp vụ, schema database, file upload, phiên đăng nhập cache và sự kiện Kafka của DPRENTAL. Migration sẽ tạo lại schema và có thể tạo dữ liệu tham chiếu hệ thống. Volume TLS, cấu hình và khóa JWT được giữ nguyên. Script kiểm tra nhãn project trước khi thao tác volume.

Script dừng stack, sao lưu và kiểm tra bốn volume dữ liệu cùng volume Meilisearch nếu có cùng cấu hình triển khai, rồi mới xóa các volume đã xác định. Bản sao lưu lưu dưới `/opt/dprental/dev/backups`, có quyền truy cập hạn chế và không tự bị dọn. Cần đủ dung lượng đĩa để lưu snapshot. Đây là bản sao lưu tại server, chưa phải bản sao lưu ở nơi khác.

Cần Docker, Python 3, flock và tar. Image triển khai và migration phải có sẵn hoặc Docker phải tải được. Không có thao tác prune toàn hệ thống.

Nếu thất bại, stack có thể đang dừng hoặc mới dọn một phần. Giữ lại đường dẫn backup đã in; không chạy lại ngay vì phiên sau có thể sao lưu trạng thái đang dở. Để khôi phục, dừng stack, kiểm tra `SHA256SUMS`, tạo lại các volume dữ liệu rỗng với đúng tên và nhãn Compose, rồi giải nén từng archive vào volume tương ứng. Dùng đúng image PostgreSQL đã ghi trong cấu hình backup làm công cụ tar. Khôi phục tất cả volume dữ liệu từ cùng một snapshot trước khi khởi động lại cấu hình release gốc. Không trộn Kafka cũ với database mới.

`up --wait` chỉ xác nhận các kiểm tra khởi động/health của Compose. Cần kiểm tra API ứng dụng sau khi dọn. Chép script lên VPS không tự chạy `--execute`.

Sau khi dọn, tạo admin và dữ liệu demo qua [bộ seed API](seed-data/README.md). Dùng thư mục state mới cho database đã reset.

Meilisearch API keys là cấu hình: reset lưu UID, quyền và phạm vi index vào backup riêng có quyền 0600; sau khi xóa volume, tạo lại key cùng UID với master key hiện hữu và kiểm tra key Inventory trước khi khởi động ứng dụng. Index và task history cũ được dọn; bootstrap sẽ dựng generation mới từ PostgreSQL.
