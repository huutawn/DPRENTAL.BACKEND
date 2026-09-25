# 2. Xem thông tin Media (Media Metadata)

## 2.1. User Story

- Là một người dùng đang sử dụng chức năng có tệp đính kèm, tôi muốn xem các thông tin cơ bản của tệp để biết tên tệp, loại tệp, dung lượng và trạng thái hiện tại.

- Là một Business Service, tôi muốn lấy thông tin của Media thông qua MediaId để kiểm tra Media có tồn tại, đúng mục đích sử dụng và đang ở trạng thái phù hợp trước khi sử dụng trong nghiệp vụ.

- Là một Business Service, tôi muốn chỉ nhận các thông tin Media cần thiết cho nghiệp vụ mà không phụ thuộc vào vị trí lưu trữ vật lý hoặc nhà cung cấp Object Storage.

## 2.2. Đầu vào

| **STT** | **Tên trường**      | **Kiểu dữ liệu**  | **Bắt buộc**   | **Mô tả / ràng buộc**                                                             |
|---------|---------------------|-------------------|----------------|-----------------------------------------------------------------------------------|
| 1       | MediaId             | Định danh         | Có             | Định danh Media cần xem                                                           |
| 2       | Đối tượng nghiệp vụ | Định danh         | Theo nghiệp vụ | Được Business Service sử dụng để xác định Media có thuộc đúng đối tượng hay không |
| 3       | Người dùng hiện tại | Hệ thống xác định | Có             | Dùng để kiểm tra quyền tại Business Service                                       |
| 4       | Service yêu cầu     | Hệ thống xác định | Có             | MediaService xác định từ danh tính service đã được xác thực                       |

Người dùng không nhập trực tiếp các thông tin như:

- Bucket;

- ObjectKey;

- Storage Provider;

- đường dẫn lưu trữ nội bộ.

## 2.3. Đầu ra

Khi Media tồn tại và caller được phép xem, hệ thống có thể trả các thông tin:

- MediaId.

- Tên tệp.

- Loại nội dung.

- Dung lượng tệp.

- Mục đích sử dụng.

- Trạng thái Media.

- Trạng thái hiển thị Private/Public.

- Ngày tạo.

- Kích thước ảnh nếu có và cần hiển thị.

Ví dụ:

```text
Tên tệp: may-xuc-komatsu.jpg
Loại tệp: JPEG Image
Dung lượng: 1.8 MB
Mục đích: Ảnh thiết bị
Trạng thái: Sẵn sàng
Ngày tải: 25/09/2026
```

Khi Media không tồn tại hoặc người dùng không được phép xem:

- không hiển thị thông tin Media;

- hệ thống trả kết quả lỗi phù hợp.

Khi Media chưa sẵn sàng:

- không được xem như một Media hợp lệ để sử dụng trong nghiệp vụ;

- trạng thái hiện tại được xử lý theo quy tắc của chức năng gọi.

## 2.4. Tiêu chí chấp nhận

- Hệ thống cho phép lấy thông tin Media bằng MediaId.

- MediaId phải xác định duy nhất một Media.

- Khi Media tồn tại và người dùng có quyền phù hợp, hệ thống hiển thị đúng metadata của Media.

- Tên file hiển thị phải là tên file được ghi nhận khi upload, không phải ObjectKey nội bộ.

- Dung lượng hiển thị phải phản ánh dung lượng thực tế của file đã được hệ thống ghi nhận.

- Loại file hiển thị phải dựa trên thông tin Media đã được hệ thống xác nhận.

- Hệ thống phải hiển thị đúng trạng thái hiện tại của Media.

- Media ở trạng thái Ready được xem là sẵn sàng để sử dụng.

- Media ở trạng thái PendingUpload không được coi là đã upload hoàn tất.

- Media ở trạng thái Rejected không được sử dụng trong nghiệp vụ.

- Media ở trạng thái Deleted không được hiển thị như Media đang hoạt động.

- Business Service phải kiểm tra Media thuộc đúng đối tượng nghiệp vụ trước khi sử dụng MediaId.

- Người dùng không được sử dụng việc biết MediaId làm căn cứ để xem Media mà không qua kiểm tra quyền.

- Response cho người dùng hoặc Business Service không được để lộ:

  - Bucket;

  - ObjectKey;

  - credential;

  - secret;

  - đường dẫn lưu trữ nội bộ.

- Chức năng xem metadata không tự động cấp quyền tải file.

## 2.5. Nguyên tắc hoạt động

### **a. Nguyên tắc xác định Media**

Mỗi Media được xác định bằng một MediaId duy nhất.

Ví dụ:

MediaId = 0199...abc

Business Service sử dụng MediaId để yêu cầu thông tin Media.

Không sử dụng:

- tên file;

- URL;

- ObjectKey

làm định danh nghiệp vụ chính.

### **b. Nguyên tắc kiểm tra quyền**

Quyền xem Media về mặt nghiệp vụ được xác định tại Business Service sở hữu entity.

Ví dụ:

Người dùng đang xem Equipment A.

CatalogService phải kiểm tra:

- người dùng có quyền xem Equipment A;

- MediaId X thực sự được tham chiếu bởi Equipment A.

Sau đó mới sử dụng thông tin Media tương ứng.

MediaService không tự quyết định:

> User này có được xem Equipment này hay không.

MediaService chỉ kiểm tra các thông tin kỹ thuật và mối quan hệ Media đã được khai báo.

### **c. Nguyên tắc hiển thị metadata**

Metadata dùng cho giao diện hoặc nghiệp vụ chỉ bao gồm thông tin cần thiết.

Có thể hiển thị:

- tên file;

- loại file;

- dung lượng;

- purpose;

- trạng thái;

- ngày tạo;

- kích thước ảnh nếu cần.

Không hiển thị:

- Storage Provider;

- Bucket;

- ObjectKey;

- checksum nội bộ;

- credential;

- thông tin kỹ thuật không cần thiết.

### **d. Nguyên tắc trạng thái Media**

Thông tin Media phải phản ánh đúng trạng thái hiện tại.

**PendingUpload**

Tệp chưa hoàn thành quá trình upload/xác minh.

Không được coi là tệp đã sẵn sàng sử dụng.

**Ready**

Tệp đã hoàn tất upload và kiểm tra.

Có thể được Business Service sử dụng nếu đáp ứng business rule.

**Rejected**

Tệp không đạt yêu cầu.

Không được tạo reference nghiệp vụ mới.

**Deleted**

Media đã được xóa về mặt logic.

Không được hiển thị như Media đang hoạt động.

### **e. Nguyên tắc Purpose**

Mỗi Media có một MediaPurpose.

Ví dụ:

- user-avatar;

- organization-logo;

- business-license;

- equipment-image;

- equipment-document;

- qc-evidence.

Business Service phải kiểm tra MediaPurpose phù hợp với nghiệp vụ.

Ví dụ:

Không được sử dụng:

purpose = user-avatar

làm:

equipment-image

chỉ vì người dùng có MediaId.

### **f. Nguyên tắc Owner**

Media được liên kết với:

- OwnerService;

- OwnerEntityType;

- OwnerEntityId.

Ví dụ:

OwnerService = CatalogService

OwnerEntityType = Equipment

OwnerEntityId = equipment-123

Khi CatalogService sử dụng MediaId cho Equipment khác, hệ thống phải kiểm tra sự phù hợp của owner.

MediaId không được tự do chuyển sang một entity khác nếu không có nghiệp vụ cho phép.

### **g. Nguyên tắc metadata và nội dung file**

Metadata và nội dung binary là hai khái niệm riêng.

Chức năng này chỉ phục vụ:

> Xem thông tin Media.

Không đồng nghĩa với:

> Tải nội dung Media.

## 2.6. Mô tả yêu cầu

### **a. Thông tin Media**

| **STT** | **Thông tin**    | **Mô tả**                      | **Quy tắc**                                                                          |
|---------|------------------|--------------------------------|--------------------------------------------------------------------------------------|
| 1       | MediaId          | Định danh Media                | Duy nhất, không cho người dùng chỉnh sửa                                             |
| 2       | Tên tệp          | Tên file khi người dùng upload | Hiển thị tên đã được hệ thống xử lý an toàn                                          |
| 3       | Loại tệp         | Loại nội dung của Media        | Ví dụ JPG, PNG, PDF                                                                  |
| 4       | Dung lượng       | Dung lượng thực tế của file    | Hiển thị theo KB/MB phù hợp                                                          |
| 5       | Mục đích sử dụng | MediaPurpose                   | Hiển thị theo tên nghiệp vụ phù hợp nếu cần                                          |
| 6       | Trạng thái       | Trạng thái Media               | PendingUpload / Ready / Rejected / Deleted                                           |
| 7       | Quyền hiển thị   | Private/Public                 | Không đồng nghĩa với quyền nghiệp vụ của người dùng                                  |
| 8       | Ngày tải         | Thời điểm Media được tạo       | Hiển thị theo định dạng thời gian của hệ thống                                       |
| 9       | Kích thước ảnh   | Chiều rộng × chiều cao         | Chỉ hiển thị đối với ảnh và khi thông tin có sẵn                                     |
| 10      | Thao tác xem/tải | Truy cập nội dung file         | Chỉ hiển thị nếu chức năng nghiệp vụ và quyền truy cập cho phép; xử lý theo Module 3 |

#### 

#### 

#### 

### **b. Quy tắc hiển thị theo trạng thái**

| **STT** | **Trạng thái** | **Hành vi**                                                                    |
|---------|----------------|--------------------------------------------------------------------------------|
| 1       | PendingUpload  | Hiển thị tệp đang được xử lý/chưa hoàn tất; không cho sử dụng nghiệp vụ        |
| 2       | Ready          | Hiển thị thông tin bình thường; cho phép sử dụng nếu Business Service cho phép |
| 3       | Rejected       | Hiển thị lỗi hoặc tệp không hợp lệ tại workflow upload; không cho sử dụng      |
| 4       | Deleted        | Không hiển thị trong dữ liệu hoạt động thông thường                            |
| 5       | Không tìm thấy | Hiển thị thông báo phù hợp, không hiển thị metadata giả                        |

### **c. Quy tắc khi Business Service sử dụng MediaId**

Trước khi gắn MediaId vào dữ liệu nghiệp vụ, Business Service phải xác nhận tối thiểu:

- Media tồn tại;

- Media ở trạng thái Ready;

- MediaPurpose phù hợp;

- Media thuộc đúng OwnerService;

- Media thuộc đúng OwnerEntityType;

- Media thuộc đúng OwnerEntityId.

Ví dụ:

```text
Tạo Equipment
↓
imageMediaId = M1
↓
CatalogService kiểm tra:
M1 tồn tại? Có
M1 Ready? Có
Purpose equipment-image? Có
Owner = Equipment X? Có
↓
Cho phép lưu reference
Nếu một trong các điều kiện không phù hợp:
→ không lưu MediaId vào entity.
```

### **d. Thông báo**

| **STT** | **Trường hợp**          | **Nội dung đề xuất**                 |
|---------|-------------------------|--------------------------------------|
| 1       | Media không tồn tại     | Không tìm thấy tệp.                  |
| 2       | Media chưa hoàn tất     | Tệp chưa sẵn sàng để sử dụng.        |
| 3       | Media bị từ chối        | Tệp không hợp lệ.                    |
| 4       | Media không còn sử dụng | Tệp không còn khả dụng.              |
| 5       | Không có quyền          | Bạn không có quyền truy cập tệp này. |

## 2.7. Ràng buộc

Mỗi Media phải có một MediaId duy nhất.

Chỉ Media ở trạng thái Ready được tạo tham chiếu nghiệp vụ mới.

Việc biết MediaId không đồng nghĩa người dùng có quyền xem hoặc tải Media.

Business Service chịu trách nhiệm xác định quyền của người dùng trên entity nghiệp vụ.

MediaPurpose phải phù hợp với nghiệp vụ sử dụng Media.

Media phải thuộc đúng OwnerService, OwnerEntityType và OwnerEntityId khi được sử dụng.

Không trả Bucket, ObjectKey hoặc thông tin credential của Object Storage cho client.

Không sử dụng URL lưu trữ làm định danh nghiệp vụ.

Metadata không được chứa thông tin nhạy cảm không cần thiết.

Media ở trạng thái Deleted không được xem như Media hoạt động.

Media ở trạng thái Rejected không được sử dụng trong nghiệp vụ.

Chức năng xem metadata không tự động cấp quyền truy cập binary.
