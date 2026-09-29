# 3. Truy cập và tải Media

## 3.1. User Story

- Là một người dùng có quyền truy cập dữ liệu nghiệp vụ, tôi muốn xem hoặc tải tệp được đính kèm để sử dụng các thông tin, hình ảnh hoặc tài liệu liên quan.

- Là một người dùng, tôi muốn hệ thống chỉ cho phép truy cập những Media mà tôi có quyền xem, nhằm bảo vệ các tài liệu và bằng chứng riêng tư.

- Là một Business Service, tôi muốn yêu cầu quyền truy cập Media sau khi đã kiểm tra quyền người dùng trên đối tượng nghiệp vụ, để nội dung file không bị truy cập chỉ dựa vào việc biết MediaId.

## 3.2. Đầu vào

| **STT** | **Tên trường**  | **Kiểu dữ liệu** | **Bắt buộc** | **Mô tả / ràng buộc**                                                         |
|---------|-----------------|------------------|--------------|-------------------------------------------------------------------------------|
| 1       | mediaId         | UUID/ULID        | Có           | Định danh duy nhất của Media cần truy cập                                     |
| 2       | action          | Chuỗi / Enum     | Có           | Hành động yêu cầu trên Media. MVP hỗ trợ read                                 |

Ngoài các trường trên, hệ thống tự xác định từ ngữ cảnh xác thực:

| **STT** | **Thông tin**       | **Nguồn**              | **Mô tả**                                                                         |
|---------|---------------------|------------------------|-----------------------------------------------------------------------------------|
| 1       | Người dùng hiện tại | Authentication Context | Business Service sử dụng để kiểm tra quyền người dùng trên đối tượng nghiệp vụ    |
| 2       | Service identity    | service API key qua Gateway | MediaService xác thực service và scope `media.read`                            |
| 3       | Quyền nghiệp vụ     | Business Service       | Xác định người dùng có được xem/tải Media thuộc entity đang yêu cầu hay không     |

## 3.3. Đầu ra

### **a. Truy cập thành công đối với Media Private**

Hệ thống trả thông tin truy cập tạm thời gồm:

- URL truy cập tệp;

- thời điểm URL hết hạn;

- MediaId tương ứng nếu cần.

Người dùng có thể:

- xem nội dung;

- preview nếu loại file và chức năng hỗ trợ;

- tải file nếu nghiệp vụ cho phép.

### **b. Truy cập thành công đối với Media Public**

Nếu Media đang ở trạng thái Public và đã được Business Service cho phép công bố:

- hệ thống cho phép truy cập theo cơ chế public media đã cấu hình;

- không để lộ vị trí lưu trữ nội bộ;

- không yêu cầu Business Service cấp lại quyền nghiệp vụ ở những trường hợp public được thiết kế cho phép truy cập công khai.

Cơ chế phân phối Public Media cụ thể được xác định theo cấu hình hệ thống.

### **c. Không có quyền truy cập**

- Không trả nội dung tệp.

- Không trả URL truy cập.

- Hiển thị thông báo phù hợp.

Ví dụ:

> “Bạn không có quyền truy cập tệp này.”

Hoặc trong trường hợp cần che giấu sự tồn tại của tài nguyên:

> “Không tìm thấy tệp hoặc bạn không có quyền truy cập.”

### **d. Media không sẵn sàng**

Nếu Media:

- đang tải;

- bị từ chối;

- đã xóa;

thì không cấp quyền truy cập nội dung.

### **e. URL truy cập hết hạn**

- URL cũ không còn sử dụng được.

- Người dùng phải yêu cầu truy cập lại.

- Hệ thống có thể cấp URL mới nếu người dùng vẫn còn quyền.

## 3.4. Tiêu chí chấp nhận

- Hệ thống chỉ cho phép truy cập Media khi Media tồn tại và ở trạng thái Ready.

- Người dùng truy cập Media Private phải được Business Service xác nhận có quyền trên đối tượng nghiệp vụ liên quan.

- Việc biết MediaId không đủ để truy cập nội dung Media.

- Business Service phải đối chiếu MediaId với entity nghiệp vụ trước khi yêu cầu quyền truy cập.

- MediaService chỉ cấp URL Private cho service có service API key hợp lệ và scope `media.read`; Business Service đối chiếu mediaId với entity liên quan.

- Media ở trạng thái PendingUpload không được cấp quyền xem hoặc tải.

- Media ở trạng thái Rejected không được cấp quyền xem hoặc tải.

- Media ở trạng thái Deleted không được cấp quyền truy cập mới.

- Khi truy cập Media Private thành công, URL truy cập phải có thời hạn.

- URL hết hạn không được tiếp tục sử dụng.

- Người dùng vẫn còn quyền có thể yêu cầu hệ thống cấp lại quyền truy cập sau khi URL cũ hết hạn.

- URL của Media Private không được sử dụng như một URL công khai lâu dài.

- Metadata và phản hồi lỗi không được để lộ:

  - Bucket;

  - ObjectKey;

  - storage credential hoặc URL có chữ ký;

  - thông tin xác thực nội bộ.

- Nếu Media Public, hệ thống chỉ cho phép public access khi:

  - Media ở trạng thái Ready;


  - Business Service đã xác định Media được công bố.

- Chuyển Media từ Public về Private phải ngăn việc cấp quyền public mới.

- URL tạm thời đã được cấp trước đó có thể còn hiệu lực cho đến khi hết hạn; hệ thống không được cam kết thu hồi tức thời nếu chưa có cơ chế riêng.

- Việc xem metadata không tự động cấp quyền tải binary.

## 3.5. Nguyên tắc hoạt động

### **a. Nguyên tắc kiểm tra quyền truy cập**

Đối với Media Private, quyền truy cập được kiểm tra theo hai lớp.

**Business Service**

Kiểm tra quyền nghiệp vụ của người dùng.

Ví dụ:

Người dùng muốn xem tài liệu của Equipment A.

CatalogService kiểm tra:

- người dùng có quyền xem Equipment A;

- MediaId đang thuộc Equipment A;

- thao tác xem/tải được phép.

Sau khi hợp lệ mới yêu cầu MediaService cấp quyền truy cập.

**MediaService**

MediaService kiểm tra:

- Media tồn tại;

- Media ở trạng thái Ready;

- service API key có scope `media.read`;

- Visibility phù hợp;

- yêu cầu truy cập hợp lệ.

### **b. Nguyên tắc Media Private**

Media Private không có đường dẫn công khai cố định.

Khi người dùng cần xem hoặc tải:

```text
Người dùng
↓
Business Service
↓
Kiểm tra quyền
↓
MediaService
↓
Cấp quyền truy cập có thời hạn
↓
Người dùng xem/tải file
```

Quyền truy cập chỉ tồn tại trong thời gian giới hạn.

Hết thời gian:

→ phải yêu cầu lại.

### **c. Nguyên tắc Media Public**

Media được đánh dấu Public chỉ khi:

- Business Service đã quyết định công bố;

- Business Service quyết định Media được công bố;

- Media đang Ready.

Ví dụ phù hợp:

- avatar;

- organization logo;

- ảnh thiết bị đã được công bố.

Ví dụ mặc định không public:

- business license;

- QC evidence;

- delivery evidence;

- incident evidence.

Bucket vẫn là Private. Trong MVP, URL GET ký ngắn hạn của MinIO có thể chứa Bucket và ObjectKey ngẫu nhiên theo giao thức S3; xem ngoại lệ ở cuối tài liệu.

### **d. Nguyên tắc URL truy cập**

URL dùng để truy cập nội dung chỉ là phương tiện truy cập tạm thời hoặc delivery URL.

Không sử dụng URL làm:

- định danh Media;

- khóa liên kết business;

- dữ liệu tham chiếu lâu dài giữa các service.

Business Service vẫn lưu:

MediaId

không lưu URL.

### **e. Nguyên tắc xem và tải**

Business Service quyết định thao tác nào được cung cấp cho người dùng.

Có thể bao gồm:

- Xem.

- Preview.

- Tải xuống.

Không phải mọi Media đều bắt buộc cho tải xuống.

Ví dụ:

- ảnh thiết bị: xem trực tiếp;

- PDF tài liệu: preview hoặc tải;

- bằng chứng QC: xem/tải nếu người dùng có quyền;

- business license: chỉ người có quyền phù hợp được xem.

### **f. Nguyên tắc hết hạn quyền truy cập**

Đối với quyền truy cập có thời hạn:

- thời gian hiệu lực được cấu hình;

- sau khi hết hạn URL không còn giá trị;

- hệ thống không tự kéo dài URL cũ;

- nếu người dùng còn quyền, phải yêu cầu quyền truy cập mới.

### **g. Nguyên tắc khi quyền người dùng thay đổi**

Mỗi lần yêu cầu cấp quyền truy cập mới, Business Service phải kiểm tra quyền hiện tại.

Không được dựa vào việc:

> Người dùng từng xem file trước đây

để kết luận:

> Hiện tại vẫn còn quyền.

URL đã cấp trước khi quyền thay đổi có thể tiếp tục sử dụng tới khi hết hạn.

Do đó URL Private phải có thời gian tồn tại giới hạn.

### **h. Nguyên tắc ghi nhận truy cập**

Hệ thống phải ghi nhận đủ thông tin phục vụ truy vết việc cấp quyền truy cập Media.

Có thể bao gồm:

- MediaId;

- người dùng/service yêu cầu;

- hành động;

- thời điểm;

- kết quả thành công/thất bại.

Không ghi đầy đủ URL truy cập có chữ ký vào log.

## 3.6. Mô tả yêu cầu

### **a. Xem tệp**

| **STT** | **Tên**       | **Mô tả**                                 | **Control**              | **Giá trị mặc định** | **Bắt buộc** | **Quy tắc**                                           |
|---------|---------------|-------------------------------------------|--------------------------|----------------------|--------------|-------------------------------------------------------|
| 1       | Tên tệp       | Hiển thị tên Media                        | Text/Link                | Theo Media           | —            | Hiển thị tên file đã được ghi nhận                    |
| 2       | Xem tệp       | Xem nội dung Media                        | Button/Link              | —                    | —            | Chỉ hiển thị khi người dùng có quyền                  |
| 3       | Xem trước     | Preview nội dung                          | Preview/Image/PDF Viewer | —                    | —            | Chỉ hỗ trợ loại tệp phù hợp                           |
| 4       | Tải xuống     | Tải file về thiết bị                      | Button/Icon              | —                    | —            | Chỉ cho phép khi nghiệp vụ và quyền truy cập cho phép |
| 5       | Trạng thái    | Cho biết Media hiện có khả dụng hay không | Status                   | Tự động              | —            | Ready mới cho phép truy cập                           |
| 6       | Thông báo lỗi | Hiển thị lỗi truy cập                     | Message/Toast            | Ẩn                   | —            | Hiện khi Media không khả dụng hoặc không có quyền     |

### **b. Quy tắc khi người dùng chọn Xem**

1.  Người dùng chọn Media từ màn hình nghiệp vụ.

2.  Business Service kiểm tra quyền.

3.  Nếu không có quyền:

    - không cấp URL;

    - hiển thị lỗi.

4.  Nếu Media không Ready:

    - không cho xem.

5.  Nếu hợp lệ:

    - hệ thống lấy quyền truy cập Media;

    - hiển thị ảnh/preview nếu hỗ trợ.

### **c. Quy tắc khi người dùng chọn Tải xuống**

1.  Người dùng chọn **Tải xuống**.

2.  Hệ thống kiểm tra quyền truy cập tại thời điểm yêu cầu.

3.  Nếu hợp lệ:

    - hệ thống cấp quyền tải file;

    - Browser bắt đầu tải file.

4.  Tên file tải xuống sử dụng tên file hiển thị của Media, không sử dụng ObjectKey.

### **d. Quy tắc Preview**

Preview phụ thuộc loại Media.

| **Loại Media**              | **Hành vi đề xuất**                    |
|-----------------------------|----------------------------------------|
| JPG / PNG / WebP nếu hỗ trợ | Hiển thị ảnh                           |
| PDF                         | Có thể mở PDF viewer/browser           |
| DOC / DOCX                  | Tải xuống hoặc mở theo khả năng client |
| XLS / XLSX                  | Tải xuống hoặc mở theo khả năng client |
| Loại không preview được     | Chỉ hiển thị thông tin và nút tải      |

Không yêu cầu MediaService thực hiện:

- chỉnh sửa document;

- convert Office file;

- OCR.

#### 

#### 

### **e. Thông báo**

| **STT** | **Trường hợp**         | **Nội dung đề xuất**                             |
|---------|------------------------|--------------------------------------------------|
| 1       | Không tìm thấy Media   | Không tìm thấy tệp.                              |
| 2       | Không có quyền         | Bạn không có quyền truy cập tệp này.             |
| 3       | Media chưa sẵn sàng    | Tệp chưa sẵn sàng để sử dụng.                    |
| 4       | Media đã xóa           | Tệp không còn khả dụng.                          |
| 5       | Quyền truy cập hết hạn | Liên kết truy cập đã hết hạn. Vui lòng thử lại.  |
| 6       | Lỗi tải tệp            | Không thể tải tệp lúc này. Vui lòng thử lại sau. |

## 3.7. Ràng buộc

- Chỉ Media ở trạng thái Ready được phép cấp quyền truy cập mới.

- Media Private không được có URL công khai cố định.

- Người dùng phải được Business Service xác thực quyền trước khi truy cập Media Private.

- Việc biết MediaId không đồng nghĩa có quyền xem hoặc tải Media.

- URL truy cập Media Private phải có thời hạn.

- URL hết hạn không được tự động gia hạn; phải yêu cầu quyền truy cập mới.

- Không sử dụng URL truy cập làm reference nghiệp vụ.

- Các Business Service chỉ lưu MediaId.

- Metadata và URL Public ổn định không expose ObjectKey, Bucket hoặc storage credential. URL GET ký ngắn hạn là ngoại lệ giao thức S3 được mô tả bên dưới.

- Media ở trạng thái Deleted hoặc Rejected không được cấp quyền truy cập mới.

- Media Public chỉ được công bố khi Business Service đã quyết định publish.

- Public Media không đồng nghĩa Object Storage phải được cấu hình public.

- Thao tác Preview phụ thuộc loại tệp và khả năng của client; MediaService không chịu trách nhiệm chỉnh sửa hoặc chuyển đổi document trong MVP.

## 3.8. Hợp đồng API MVP

- `GET /api/v1/media/{mediaId}`: Business Service có service API key scope `media.read` lấy metadata qua Gateway. `Deleted` trả 404. `PendingUpload` và `Rejected` có status nhưng không giả định MIME/dung lượng chưa xác minh là dữ liệu thực tế. Endpoint này không trả URL file.
- `POST /api/v1/media/{mediaId}/access`: Business Service có scope `media.read` gửi `{ "action": "read", "download": false }`. Sau khi Business Service đã kiểm tra quyền người dùng và mediaId có trong entity, MediaService kiểm tra status rồi trả `{ "mediaId": "...", "url": "...", "expiresAtUtc": "..." }`. `download: true` đặt Content-Disposition để trình duyệt tải với tên file gốc đã được làm sạch.
- Response của `POST /api/v1/media/uploads` có `previewUrl` và `previewUrlExpiresAtUtc` để chính người upload xem file vừa lưu trước khi entity nghiệp vụ được ghi. URL này là GET ký ngắn hạn, không thay thế quyền đọc qua Business Service sau khi entity đã được lưu. Nếu ký URL lỗi sau khi upload thành công, hai trường preview là null.
- `GET /api/v1/media/{mediaId}/content`: URL Public ổn định, không cần token. MediaService chỉ chuyển hướng (302) tới URL GET ký ngắn hạn nếu Media đang `Ready`, `Public`; `?download=true` yêu cầu tải xuống. Sau khi chuyển Private, request mới tới URL này trả 404. URL đã ký trước đó có thể còn hiệu lực đến khi hết hạn.
- URL đọc ký sẵn mặc định sống 300 giây, cấu hình bằng `ObjectStorage:ReadUrlExpirySeconds` trong khoảng 1–900 giây. Bucket MinIO vẫn Private. Business Service lưu `mediaId`, không lưu URL đọc; client xin URL mới khi hết hạn.
- URL S3 ký sẵn là ngoại lệ kỹ thuật đã chọn cho MVP: nó có thể chứa Bucket, ObjectKey ngẫu nhiên và access-key ID, nhưng không chứa secret key. Không ghi URL này vào log, không lưu lâu dài hoặc dùng làm định danh nghiệp vụ. Để tránh lộ cả đường dẫn MinIO cần một cơ chế proxy/CDN khác trong giai đoạn sau.
