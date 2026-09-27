# 6. Xóa và quản lý vòng đời

## 6.1. User Story

- Là một người dùng có quyền chỉnh sửa dữ liệu nghiệp vụ, tôi muốn loại bỏ hình ảnh hoặc tài liệu không còn được sử dụng để dữ liệu hiển thị luôn chính xác.

- Là một Business Service, tôi muốn xóa Media sau khi Media không còn được entity nghiệp vụ tham chiếu để tránh giữ các tệp không cần thiết.

- Là hệ thống, tôi muốn tự động xử lý các Media bị bỏ dở, hết hạn hoặc không còn được sử dụng để hạn chế dữ liệu rác trong Object Storage.

- Là hệ thống, tôi muốn việc xóa Media được thực hiện an toàn theo vòng đời thay vì xóa binary ngay lập tức để hạn chế mất dữ liệu do lỗi hoặc trạng thái nghiệp vụ chưa xác định.

## 6.2. Đầu vào

Thông tin yêu cầu xóa Media:

| **STT** | **Tên trường**  | **Kiểu dữ liệu** | **Bắt buộc** | **Mô tả / ràng buộc**                           |
|---------|-----------------|------------------|--------------|-------------------------------------------------|
| 1       | mediaId         | UUID/ULID        | Có           | Định danh Media cần xóa                         |
| 2       | ownerEntityType | Chuỗi            | Có           | Loại đối tượng nghiệp vụ đang sở hữu Media      |
| 3       | ownerEntityId   | UUID/ULID        | Có           | Định danh đối tượng nghiệp vụ đang sở hữu Media |

**Hệ thống tự xác định:**

| **Thông tin**       | **Nguồn**              | **Mô tả**                                                   |
|---------------------|------------------------|-------------------------------------------------------------|
| Người dùng hiện tại | Authentication Context | Business Service dùng để kiểm tra quyền xóa/thay đổi entity |
| ownerService        | Service Identity       | MediaService xác định từ service đang gọi                   |
| status              | Media Metadata         | Xác định Media có thể thực hiện thao tác xóa hay không      |
| purpose             | Media Metadata         | Phục vụ kiểm tra Media thuộc đúng ngữ cảnh nghiệp vụ        |

## 6.3. Đầu ra

Xóa thành công

Hệ thống:

- chuyển Media sang trạng thái deleted;

- Media không còn được sử dụng cho các business reference mới;

- không cấp quyền truy cập mới;

- đưa binary vào quy trình dọn dẹp theo chính sách vòng đời;

- trả kết quả thành công.

Thông báo:

> “Xóa tệp thành công.”

Xóa lại Media đã xóa

Nếu Media đã ở trạng thái:

deleted

và nhận lại cùng yêu cầu xóa:

- hệ thống vẫn coi thao tác thành công;

- không tạo lỗi ngoài ý muốn;

- không tạo thêm Media hoặc dữ liệu mới.

Không có quyền

- không thay đổi trạng thái Media;

- không xóa binary;

- trả thông báo phù hợp.

Media không tồn tại

- không thực hiện thay đổi;

- trả kết quả không tìm thấy phù hợp với error contract.

## 6.4. Tiêu chí chấp nhận

- Người dùng chỉ được xóa Media khi có quyền chỉnh sửa/xóa Media trên đối tượng nghiệp vụ liên quan.

- Business Service phải kiểm tra quyền người dùng trước khi yêu cầu MediaService xóa Media.

- Media phải thuộc đúng:

  - ownerService;

  - ownerEntityType;

  - ownerEntityId.

- Business Service phải loại bỏ business reference đến Media trước khi yêu cầu xóa Media.

- MediaService không được tự chỉnh sửa dữ liệu của Business Service để loại bỏ reference.

- Khi xóa thành công, Media phải chuyển sang trạng thái deleted.

- Media deleted không được tạo business reference mới.

- Media deleted không được cấp quyền truy cập mới.

- Việc xóa logic không bắt buộc phải xóa binary ngay tại thời điểm người dùng thực hiện thao tác.

- Binary của Media đã xóa phải được xử lý theo retention/cleanup policy của hệ thống.

- Gọi xóa lại Media đã deleted phải không gây lỗi ngoài ý muốn.

- Xóa Media không được tạo MediaId mới.

- Nếu Media đang được thay thế, Media cũ chỉ được xóa sau khi reference sang Media mới đã được cập nhật thành công.

- Media bị upload nhưng không hoàn tất phải được hệ thống xử lý sau khi upload session hết hạn.

- Media đã ready nhưng không được sử dụng bởi nghiệp vụ phải có cơ chế xác định và cleanup.

- Hệ thống không được tự xóa Media khi chưa xác định chắc chắn Media không còn được nghiệp vụ sử dụng.

- Sau khi Media bị xóa, URL truy cập mới không được cấp.

- URL truy cập tạm thời đã được cấp trước thời điểm xóa có thể còn hiệu lực cho đến khi hết hạn nếu hệ thống chưa hỗ trợ revoke tức thời.

## 6.5. Nguyên tắc hoạt động

### **a. Nguyên tắc xóa business reference trước**

MediaService không sở hữu dữ liệu nghiệp vụ.

Ví dụ:

```text
Equipment
└── imageMediaIds = [A, B, C]
```

Người dùng muốn xóa ảnh B.

CatalogService phải thực hiện:

```text
[A, B, C]
↓
[A, C]
```

trước.

Sau khi cập nhật business entity thành công:

```text
CatalogService
→ MediaService
→ delete media B
```

Không thực hiện ngược:

delete media B

→ sau đó mới thử xóa B khỏi Equipment

vì nếu cập nhật Equipment thất bại, entity sẽ còn tham chiếu tới Media đã bị xóa.

### **b. Nguyên tắc Logical Delete**

Xóa Media trong nghiệp vụ trước tiên là xóa logic.

ready

↓

deleted

Sau khi chuyển deleted:

- Media không còn được sử dụng;

- không cấp access mới;

- metadata cần thiết vẫn có thể được giữ lại để audit/lifecycle;

- binary chưa bắt buộc bị xóa ngay.

### **c. Nguyên tắc Physical Delete**

Việc xóa binary khỏi Object Storage được thực hiện sau theo chính sách vòng đời.

Ví dụ:

```text
Logical Delete
↓
Retention Period
↓
Physical Cleanup
```

Thời gian retention được cấu hình theo chính sách hệ thống.

Nếu chưa chốt giá trị cụ thể trong SRS:

> **Retention period: TBD / cấu hình hệ thống.**

Không tự đặt số ngày nếu business chưa xác nhận.

### **d. Nguyên tắc Deleted là trạng thái cuối**

Media đã:

deleted

không được chuyển lại thành:

ready

Nếu người dùng cần file trở lại:

- thực hiện upload mới;

- tạo mediaId mới.

Không hồi sinh Media cũ.

### **e. Nguyên tắc Media bị bỏ dở khi upload**

Trường hợp:

```text
MediaService ghi phiên upload nội bộ
↓
Request bị gián đoạn trước khi xác minh xong
↓
Session Expired
```

### **f. Nguyên tắc object đã ghi nhưng chưa xác minh xong**

Có thể xảy ra:

```text
MediaService ghi object tạm
↓
Request bị gián đoạn trước khi cập nhật Ready
↓
UploadSession Expired
```

Khi đó Object Storage có file nhưng Media chưa ready.

File này phải được xác định là file upload chưa hoàn tất và được cleanup theo policy.

### **g. Nguyên tắc Orphan Media**

Orphan Media là Media tồn tại nhưng không còn business reference hợp lệ.

Ví dụ:

```text
Upload Media
→ mediaId = A
→ status = ready
Sau đó:
POST /equipment thất bại
```

Media A có thể trở thành orphan candidate.

Hệ thống không nên xóa ngay vì có thể tồn tại trường hợp:

- timeout;

- response thất lạc;

- transaction business đã commit nhưng caller không nhận được kết quả.

Do đó phải có khoảng thời gian an toàn hoặc quá trình reconciliation trước khi cleanup.

### **h. Nguyên tắc Replace**

Khi thay:

oldMediaId → newMediaId

chỉ sau khi Business Service xác nhận entity đã sử dụng newMediaId mới được xử lý oldMediaId.

Nếu swap thất bại chắc chắn:

- Media cũ được giữ nguyên;

- Media mới trở thành ứng viên orphan.

Nếu kết quả swap chưa xác định:

- không xóa Media cũ;

- không xóa ngay Media mới;

- chờ xác minh trạng thái business reference.

### **i. Nguyên tắc xóa Media Public**

Nếu Media đang:

visibility = public

và được xóa:

- Media chuyển sang deleted;

- không cấp public access mới;

- không cấp private access mới.

Không cần thực hiện:

public → private → deleted

như hai thao tác người dùng riêng biệt.

deleted đã khiến Media không còn khả dụng.

### **j. Nguyên tắc truy cập sau khi xóa**

Sau thời điểm Media được đánh dấu deleted:

- request access mới bị từ chối;

- Media không được hiển thị như tài nguyên hoạt động;

- business entity không được tạo reference mới đến Media này.

Access URL đã phát trước đó có thể còn hiệu lực trong thời gian ngắn cho tới khi hết hạn.

Do đó URL Private phải có TTL giới hạn.

### **k. Nguyên tắc Retry**

Thao tác xóa phải an toàn khi retry.

Ví dụ:

```text
DELETE media A
→ thành công
Client timeout
DELETE media A lần nữa
```

Kết quả lần hai vẫn được coi là thành công.

Không bắt người dùng xử lý lỗi kiểu:

> “Media đã bị xóa trước đó.”

## 6.6. Mô tả yêu cầu

### **a. Xóa Media khỏi nghiệp vụ**

| **STT** | **Tên**          | **Mô tả**                      | **Control**   | **Giá trị mặc định** | **Bắt buộc** | **Quy tắc**                                        |
|---------|------------------|--------------------------------|---------------|----------------------|--------------|----------------------------------------------------|
| 1       | Tệp hiện tại     | Media đang được entity sử dụng | Preview/Text  | Theo dữ liệu         | —            | Chỉ Media người dùng có quyền thao tác             |
| 2       | Xóa              | Loại bỏ Media khỏi entity      | Icon/Button   | —                    | —            | Chỉ hiển thị khi có quyền chỉnh sửa                |
| 3       | Xác nhận xóa     | Xác nhận thao tác              | Popup/Dialog  | —                    | Có khi xóa   | Hiển thị trước khi thực hiện nếu nghiệp vụ yêu cầu |
| 4       | Hủy              | Không thực hiện xóa            | Button        | —                    | —            | Đóng popup, không thay đổi dữ liệu                 |
| 5       | Trạng thái xử lý | Thông báo kết quả              | Toast/Message | —                    | —            | Thành công hoặc lỗi tương ứng                      |

### **b. Quy tắc khi nhấn Xóa**

1.  Người dùng chọn **Xóa** trên Media.

2.  Hệ thống có thể hiển thị popup xác nhận.

3.  Business Service kiểm tra quyền người dùng.

4.  Nếu không có quyền:

    - dừng thao tác;

    - hiển thị lỗi.

5.  Nếu có quyền:

    - Business Service loại MediaId khỏi entity.

6.  Sau khi cập nhật entity thành công:

    - Business Service yêu cầu MediaService xóa Media.

7.  Media chuyển sang deleted.

8.  Hệ thống thông báo thành công.

### **c. Quy tắc khi Hủy**

Nếu người dùng chọn Hủy:

- không thay đổi business reference;

- không thay đổi Media;

- không thực hiện delete.

### **d. Xử lý Media không còn được sử dụng**

Các trường hợp hệ thống có thể đưa Media vào cleanup gồm:

| **STT** | **Trường hợp**                                     | **Hướng xử lý**                                   |
|---------|----------------------------------------------------|---------------------------------------------------|
| 1       | UploadSession hết hạn và không có file             | Đóng phiên upload và cleanup metadata theo policy |
| 2       | File đã ghi nhưng request chưa xác minh xong       | Cleanup object sau thời gian an toàn              |
| 3       | Media bị rejected                                  | Không cho sử dụng, cleanup binary theo policy     |
| 4       | Media ready nhưng business request bị hủy/thất bại | Xác định orphan trước khi cleanup                 |
| 5       | Media đã deleted                                   | Physical delete sau retention                     |
| 6       | Replace thành công                                 | Media cũ được đưa vào delete lifecycle            |

### **e. Thông báo**

| **STT** | **Trường hợp**              | **Nội dung đề xuất**                               |
|---------|-----------------------------|----------------------------------------------------|
| 1       | Xóa thành công              | Xóa tệp thành công.                                |
| 2       | Không có quyền              | Bạn không có quyền xóa tệp này.                    |
| 3       | Không tìm thấy Media        | Không tìm thấy tệp.                                |
| 4       | Media không thuộc đối tượng | Tệp không thuộc dữ liệu đang thao tác.             |
| 5       | Cập nhật nghiệp vụ thất bại | Không thể xóa tệp. Vui lòng thử lại.               |
| 6       | Lỗi hệ thống                | Không thể xử lý tệp lúc này. Vui lòng thử lại sau. |

## 6.7. Ràng buộc

- Business Service phải loại bỏ business reference trước khi yêu cầu xóa Media.

- MediaService không được trực tiếp sửa hoặc truy vấn database của Business Service để xóa reference.

- Media phải thuộc đúng ownerService, ownerEntityType và ownerEntityId.

- Xóa Media là logical delete trước; physical delete được thực hiện theo lifecycle policy.

- Media ở trạng thái deleted không được khôi phục trong MVP.

- Nếu cần sử dụng lại nội dung đã xóa, phải upload và tạo mediaId mới.

- deleted là trạng thái cuối của MediaAsset.

- Media deleted không được cấp access mới.

- Media deleted không được tạo business reference mới.

- Việc retry thao tác delete phải idempotent.

- Không được physical delete Media khi chưa chắc chắn Media không còn business reference.

- Media upload dở, Media bị bỏ quên hoặc orphan phải được cleanup theo chính sách hệ thống.

- Không xóa ngay orphan candidate nếu kết quả transaction nghiệp vụ chưa xác định.

- Thời gian retention và cleanup phải được cấu hình; nếu chưa được business xác nhận thì để ở trạng thái TBD.

- Việc xóa Media không được làm lộ Bucket, ObjectKey hoặc storage credential.

- URL đã cấp trước thời điểm delete có thể còn tồn tại tới khi hết hạn; revoke tức thời không thuộc MVP nếu chưa có yêu cầu riêng.

- Audit metadata cần thiết có thể được giữ lại sau physical delete theo chính sách lưu vết của hệ thống.
