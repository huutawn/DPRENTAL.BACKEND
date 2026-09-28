# 1. Tải tệp lên hệ thống

## 1.1. Mục tiêu

Người dùng đã đăng nhập gửi file trong **một request** tới MediaService và nhận `mediaId` ở trạng thái `Ready` sau khi hệ thống lưu và xác minh file. Upload độc lập với entity nghiệp vụ; Business Service gọi API `/verify` để kiểm tra mediaId trước khi lưu tham chiếu. Client không gọi MinIO trực tiếp và không gọi API `complete`.

## 1.2. API MVP

`POST /api/v1/media/uploads` nhận `multipart/form-data` và user token. Các phần của form:

| Trường | Bắt buộc | Ý nghĩa |
| --- | --- | --- |
| `file` | Có | Binary của ảnh hoặc PDF. MediaService lấy tên, MIME và dung lượng từ file do trình duyệt gửi. |
| `sha256Checksum` | Không | SHA-256 do client tính nếu muốn đối chiếu thêm. |
| Header `Idempotency-Key` | Không | Khóa do client tạo để gửi lại cùng request an toàn. |

MediaService không nhận hoặc lưu owner hay purpose. Giới hạn dung lượng dựa trên định dạng file; `fileName`, `contentType` và `sizeBytes` không phải các form field riêng; client chỉ chọn file.

Response thành công HTTP 200:

```json
{
  "mediaId": "<guid>",
  "status": "Ready",
  "originalFileName": "may-xuc.jpg",
  "contentType": "image/jpeg",
  "sizeBytes": 1250340,
  "previewUrl": "https://storage.example/signed-read-url",
  "previewUrlExpiresAtUtc": "2026-09-27T10:05:00Z"
}
```

Request chỉ trả thành công sau khi file đã được lưu, kiểm tra dung lượng, file signature, MIME và checksum. `previewUrl` là URL GET ký ngắn hạn để người upload xem đúng file đã lưu; chỉ cấp khi Media đã `Ready` và đúng người upload. Nếu ký URL lỗi sau khi file đã lưu, request vẫn thành công với `mediaId`, còn `previewUrl` và `previewUrlExpiresAtUtc` là null. Response không chứa URL upload hay định danh phiên nội bộ. Client dùng `mediaId` khi lưu form nghiệp vụ. Business Service gọi `GET /api/v1/media/{mediaId}/verify` bằng service token, đối chiếu người upload và định dạng file phù hợp nghiệp vụ với request nghiệp vụ trước khi lưu tham chiếu.

## 1.3. Quy tắc xử lý

- Media mới mặc định `Private`. File hợp lệ chuyển từ `PendingUpload` sang `Ready` trong cùng request HTTP. Trạng thái Pending chỉ phục vụ xử lý nội bộ và khôi phục sau lỗi, không phải bước client phải hoàn tất.
- MediaService hỗ trợ JPEG, PNG, WebP tối đa 10 MiB và PDF tối đa 20 MiB. Video, Office document, OCR và quét malware chưa thuộc MVP.
- Application xác minh dữ liệu thực tế; không tin tên, MIME hay dung lượng client khai báo. Tên file được làm sạch và không dùng làm khóa lưu trữ.
- File không hợp lệ trả lỗi, Media không trở thành `Ready`. Object tạm hoặc bản sao bị từ chối được đưa vào hàng đợi dọn dẹp. Lỗi MinIO hoặc PostgreSQL không trả một `mediaId` có thể sử dụng.
- MediaService giữ một bản ghi upload nội bộ cho mỗi request để xử lý idempotency, retry và dọn object nếu request bị gián đoạn. Bản ghi này không phải API công khai. Bản ghi cũ trong `media_upload_sessions` vẫn được giữ để cleanup an toàn, không cần migration phá dữ liệu.
- Khi có `Idempotency-Key`, cùng user + cùng key + cùng metadata và nội dung file trả cùng `mediaId` nếu upload đã thành công. Cùng key nhưng dữ liệu khác trả 409. Request còn Pending có thể thử lại trong 15 phút; sau khi hết hạn hoặc bị từ chối, dùng key mới. Không gửi key thì mỗi request là một upload mới.
- Cancellation được truyền tới thao tác lưu trữ và database. Không giữ transaction database trong lúc gọi MinIO. Cleanup worker dọn object của phiên Pending đã hết hạn; không xóa ngay Media `Ready` chỉ vì Business Service chưa lưu tham chiếu.

## 1.4. Tiêu chí chấp nhận

1. User token hợp lệ gửi form có file hợp lệ: một request trả `mediaId` `Ready`, metadata đúng và object đọc được từ MinIO.
2. Thiếu file hoặc input sai bị từ chối; user token thiếu trả 401. Service token không thay thế user token cho upload.
3. File rỗng, vượt giới hạn, signature/MIME/checksum sai không tạo Media `Ready`.
4. Retry cùng `Idempotency-Key` và cùng nội dung không tạo Media khác; trùng key nhưng nội dung khác bị từ chối.
5. Business Service chỉ sử dụng `mediaId` sau khi `/verify` xác nhận đúng trạng thái, người upload và định dạng phù hợp nghiệp vụ; service nghiệp vụ giữ quan hệ entity–media trong dữ liệu của mình.
6. Client không cần lấy presigned PUT URL, không PUT file tới MinIO và không gọi `/complete`.
