# 1. Tải tệp lên hệ thống

## 1.1. Mục tiêu

Người dùng đã đăng nhập gửi 1–10 file trong một request tới MediaService và nhận kết quả riêng cho từng file. Upload độc lập với entity nghiệp vụ; Business Service gọi `/verify` để kiểm tra mediaId trước khi lưu tham chiếu. Client không gọi MinIO trực tiếp và không gọi API `complete`.

## 1.2. API MVP

`POST /api/v1/media/uploads` nhận `multipart/form-data` và user token. Các phần của form:

| Trường | Bắt buộc | Ý nghĩa |
| --- | --- | --- |
| `files` | Có | Gửi 1–10 phần binary cùng tên trường; mỗi phần là ảnh hoặc PDF. |
| `sha256Checksums` | Không | Nếu gửi, cung cấp đúng một SHA-256 cho mỗi file, theo thứ tự tương ứng. |
| Header `Idempotency-Key` | Không | Khóa do client tạo để gửi lại cùng danh sách an toàn. |

MediaService lấy tên, MIME và dung lượng từ từng file; không nhận owner, purpose, `fileName`, `contentType` hoặc `sizeBytes` dưới dạng form field riêng. Giới hạn toàn bộ multipart là 25 MiB. JPEG, PNG, WebP tối đa 10 MiB mỗi file; PDF tối đa 20 MiB mỗi file.

Response HTTP 200 là mảng theo thứ tự file. Mỗi phần tử có `media` khi thành công hoặc `error` khi thất bại:

```json
[
  {
    "index": 0,
    "fileName": "may-xuc.jpg",
    "media": {
      "mediaId": "<guid>",
      "status": "Ready",
      "originalFileName": "may-xuc.jpg",
      "contentType": "image/jpeg",
      "sizeBytes": 1250340,
      "previewUrl": "https://storage.example/signed-read-url",
      "previewUrlExpiresAtUtc": "2026-09-27T10:05:00Z"
    },
    "error": null
  },
  {
    "index": 1,
    "fileName": "invalid.pdf",
    "media": null,
    "error": { "code": "InvalidFileSignature", "message": "File không hợp lệ." }
  }
]
```

Mỗi file chỉ có `media` sau khi lưu và kiểm tra dung lượng, signature, MIME, checksum. File lỗi không chặn các file còn lại. `previewUrl` là URL GET ký ngắn hạn cho đúng người upload; lỗi ký URL sau khi file đã `Ready` trả `previewUrl` null mà vẫn giữ `mediaId`. Response không chứa URL upload hay phiên nội bộ. Client dùng từng `mediaId` thành công khi lưu form nghiệp vụ. Business Service gọi `/verify` bằng service API key có quyền service `media` scope `verify` (hoặc quyền toàn cục `*/*`), đối chiếu người upload và định dạng file với request nghiệp vụ trước khi lưu tham chiếu.

## 1.3. Quy tắc xử lý

- Media mới mặc định `Private`. File hợp lệ chuyển từ `PendingUpload` sang `Ready` trong request HTTP. Pending chỉ phục vụ xử lý nội bộ và khôi phục sau lỗi.
- Application xác minh dữ liệu thực tế; không tin tên, MIME hay dung lượng client khai báo. Tên file được làm sạch và không dùng làm khóa lưu trữ. Video, Office document, OCR và quét malware chưa thuộc MVP.
- File không hợp lệ không trở thành `Ready`; object tạm hoặc bản sao bị từ chối được đưa vào hàng đợi dọn dẹp. Lỗi MinIO hoặc PostgreSQL không trả `mediaId` có thể sử dụng.
- MediaService giữ một bản ghi upload nội bộ cho mỗi file để xử lý idempotency, retry và dọn object nếu request bị gián đoạn. Bản ghi cũ trong `media_upload_sessions` vẫn được giữ để cleanup an toàn.
- Khi có `Idempotency-Key`, hệ thống gắn key với vị trí từng file và số file trong batch. Gửi lại cùng danh sách giữ nguyên `mediaId` của file đã thành công. Đổi nội dung, thứ tự hoặc số file trả `IdempotencyConflict` cho vị trí bị ảnh hưởng. File đã bị từ chối cần key mới khi gửi lại. Không gửi key thì mỗi file là một upload mới.
- Cancellation được truyền tới lưu trữ và database. Không giữ transaction database trong lúc gọi MinIO. Cleanup worker dọn object của phiên Pending đã hết hạn; không xóa Media `Ready` chỉ vì Business Service chưa lưu tham chiếu.

## 1.4. Tiêu chí chấp nhận

1. User token hợp lệ gửi danh sách file: response có kết quả theo từng vị trí; file hợp lệ nhận `mediaId` `Ready`, metadata đúng và object đọc được từ MinIO.
2. Thiếu file, quá 10 file hoặc số checksum không khớp bị từ chối; user token thiếu trả 401. Service token không thay thế user token cho upload.
3. File rỗng, vượt giới hạn, signature/MIME/checksum sai không tạo Media `Ready`; các file hợp lệ khác vẫn được xử lý.
4. Retry cùng `Idempotency-Key` và cùng danh sách không tạo Media khác cho file đã thành công; trùng key nhưng nội dung, thứ tự hoặc số file khác trả lỗi theo file.
5. Business Service chỉ sử dụng `mediaId` sau khi `/verify` xác nhận đúng trạng thái, người upload và định dạng phù hợp nghiệp vụ; service nghiệp vụ giữ quan hệ entity–media trong dữ liệu của mình.
6. Client không cần lấy presigned PUT URL, không PUT file tới MinIO và không gọi `/complete`.
