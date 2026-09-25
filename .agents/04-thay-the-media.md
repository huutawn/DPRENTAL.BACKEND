# 4. Thay thế

## 4.1. User Story

- Là một người dùng có quyền chỉnh sửa dữ liệu nghiệp vụ, tôi muốn thay thế hình ảnh hoặc tài liệu hiện tại bằng một tệp mới để thông tin Media của đối tượng luôn được cập nhật.

- Là một Business Service, tôi muốn Media mới được tải và kiểm tra thành công trước khi thay thế Media cũ để tránh làm mất tệp hiện tại nếu quá trình upload thất bại.

- Là một Business Service, tôi muốn việc thay thế sử dụng mediaId mới thay vì ghi đè nội dung của Media cũ để đảm bảo khả năng truy vết và quản lý vòng đời Media.

## 4.2. Đầu vào

| **STT** | **Tên trường**  | **Kiểu dữ liệu** | **Bắt buộc** | **Mô tả / ràng buộc**                                  |
|---------|-----------------|------------------|--------------|--------------------------------------------------------|
| 1       | oldMediaId      | UUID/ULID        | Có           | Media hiện đang được entity nghiệp vụ sử dụng          |
| 2       | newMediaId      | UUID/ULID        | Có           | Media mới đã được tải thành công và ở trạng thái Ready |
| 3       | ownerEntityType | Chuỗi            | Có           | Loại đối tượng nghiệp vụ đang sử dụng Media            |
| 4       | ownerEntityId   | UUID/ULID        | Có           | Định danh đối tượng nghiệp vụ đang sử dụng Media       |

**Hệ thống tự xác định:**

| **Thông tin**       | **Nguồn**              | **Mô tả**                                                           |
|---------------------|------------------------|---------------------------------------------------------------------|
| Người dùng hiện tại | Authentication Context | Business Service dùng để kiểm tra quyền chỉnh sửa entity            |
| ownerService        | Service Identity       | MediaService xác định từ Business Service đang gọi                  |
| purpose             | Metadata của Media     | Media mới phải phù hợp với mục đích sử dụng của vị trí cần thay thế |

## 4.3. Đầu ra

**Thay thế thành công**

Sau khi hoàn tất:

- entity nghiệp vụ tham chiếu đến newMediaId;

- oldMediaId không còn được entity sử dụng;

- Media mới trở thành Media đang hoạt động của nghiệp vụ;

- Media cũ được đưa vào quy trình xóa/quản lý vòng đời;

- hiển thị thông báo:

> “Cập nhật tệp thành công.”

**Thay thế thất bại**

Nếu Media mới không hợp lệ hoặc Business Service không thể cập nhật entity:

- oldMediaId vẫn được giữ nguyên;

- dữ liệu nghiệp vụ không được chuyển sang Media mới;

- người dùng nhận thông báo lỗi phù hợp.

**Trạng thái không xác định**

Nếu hệ thống không xác định chắc chắn việc cập nhật business entity đã thành công hay chưa do timeout/network:

- không xóa ngay Media cũ;

- không tự động xóa Media mới;

- hệ thống thực hiện kiểm tra/reconciliation trước khi cleanup.

## 4.4. Tiêu chí chấp nhận

Người dùng chỉ được thay thế Media khi có quyền chỉnh sửa đối tượng nghiệp vụ liên quan.

oldMediaId phải là Media hiện đang thuộc hoặc đang được entity nghiệp vụ sử dụng.

newMediaId phải tồn tại.

newMediaId phải ở trạng thái Ready.

newMediaId phải thuộc đúng ownerService.

newMediaId phải phù hợp với ownerEntityType và ownerEntityId.

newMediaId phải có purpose phù hợp với vị trí Media cần thay thế.

Không được thay thế bằng Media ở trạng thái:

- PendingUpload;

- Rejected;

- Deleted.

Hệ thống không được ghi đè binary của oldMediaId.

Tệp mới phải có mediaId riêng.

oldMediaId chỉ được loại khỏi business reference sau khi việc cập nhật sang newMediaId thành công.

Nếu cập nhật Business Service thất bại chắc chắn:

- oldMediaId tiếp tục được sử dụng;

- newMediaId không được gắn vào entity.

Nếu kết quả cập nhật không xác định:

- hệ thống không được tự động xóa một trong hai Media cho đến khi xác minh trạng thái thực tế.

Việc retry thao tác cập nhật không được làm phát sinh nhiều business reference cho cùng một vị trí Media ngoài ý muốn.

Sau khi thay thế thành công, các request nghiệp vụ tiếp theo phải sử dụng newMediaId.

oldMediaId sau khi không còn được sử dụng phải được xử lý theo chính sách xóa và lifecycle của MediaService.

## 4.5. Nguyên tắc hoạt động

### **a. Nguyên tắc không ghi đè Media**

Media được xem là immutable đối với binary.

Khi thay file:

*Media A*

không được đổi nội dung thành file B.

Thay vào đó:

```text
Media A
↓
upload file mới
↓
Media B
```

Sau đó Business Service thay reference:

media-A → media-B

Cách này giúp:

- giữ được lịch sử;

- tránh cache nhầm nội dung;

- tránh file đang được sử dụng bị thay đổi giữa chừng;

- xử lý lỗi an toàn hơn.

### **b. Nguyên tắc upload Media mới trước**

Quá trình thay thế luôn thực hiện theo thứ tự:

```text
1. Upload Media mới
2. Media mới = Ready
3. Cập nhật business reference
4. Xác nhận cập nhật thành công
5. Xử lý Media cũ
```

Không thực hiện:

Xóa Media cũ

→ rồi mới upload Media mới

vì nếu upload mới thất bại, entity sẽ mất Media đang sử dụng.

### **c. Nguyên tắc kiểm tra Media mới**

Trước khi thay reference, Business Service phải kiểm tra newMediaId:

- tồn tại;

- status = ready;

- đúng owner;

- đúng entity;

- đúng purpose.

Ví dụ:

Một Media có:

purpose = business-license

không được dùng để thay:

purpose = organization-logo

chỉ vì file cùng định dạng ảnh.

### **d. Nguyên tắc cập nhật reference**

Business Service là thành phần sở hữu reference nghiệp vụ.

Ví dụ:

```text
Organization
└── logoMediaId
```

Do đó MediaService **không tự sửa dữ liệu Organization**.

Business Service thực hiện:

```text
logoMediaId:
media-A
↓
media-B
```

### **e. Nguyên tắc xử lý Media cũ khi cập nhật thành công**

Sau khi Business Service xác nhận:

entity → newMediaId

thành công:

- Media cũ không còn được business entity sử dụng;

- Business Service có thể yêu cầu xóa Media cũ;

- Media cũ được xử lý theo Module 6 – Xóa và quản lý vòng đời Media.

Không bắt buộc physical delete ngay lập tức.

### **f. Nguyên tắc khi cập nhật thất bại**

**Trường hợp xác định chắc chắn thất bại**

Ví dụ validation hoặc database transaction rollback rõ ràng:

```text
oldMediaId vẫn được giữ
newMediaId chưa được sử dụng
```

Media mới trở thành Media chưa có reference và được xử lý theo lifecycle/orphan policy.

**Trường hợp không xác định kết quả**

Ví dụ:

- timeout;

- mất kết nối sau khi gửi request;

- client không nhận được response.

Không được suy luận ngay:

> update chắc chắn thất bại.

Hệ thống phải kiểm tra business reference trước khi cleanup.

### **g. Nguyên tắc đối với danh sách Media**

Không phải mọi nghiệp vụ đều thay một Media bằng một Media.

Ví dụ Equipment có thể có:

| imageMediaIds = \[A, B, C\] |
|-----------------------------|

Người dùng thay ảnh B bằng D:

```text
[A, B, C]
↓
[A, D, C]
```

Các Media A và C không bị ảnh hưởng.

Nếu người dùng chỉ thêm ảnh:

```text
[A, B, C]
→
[A, B, C, D]
```

## 4.6. Mô tả yêu cầu

### **a. Thay thế tệp**

| **STT** | **Tên**        | **Mô tả**                 | **Control**        | **Giá trị mặc định** | **Bắt buộc**              | **Quy tắc**                             |
|---------|----------------|---------------------------|--------------------|----------------------|---------------------------|-----------------------------------------|
| 1       | Tệp hiện tại   | Media đang được sử dụng   | Preview/Text       | Media hiện tại       | —                         | Hiển thị theo dữ liệu nghiệp vụ         |
| 2       | Thay thế       | Chọn một tệp mới          | Button/File Upload | —                    | —                         | Mở chức năng tải Media mới              |
| 3       | Tệp mới        | Tệp được chọn để thay thế | Preview/Text       | Trống                | Có khi thực hiện thay thế | Phải đáp ứng policy của vị trí hiện tại |
| 4       | Trạng thái tải | Trạng thái Media mới      | Status             | —                    | —                         | Đang tải / Đang xử lý / Hoàn tất / Lỗi  |
| 5       | Xác nhận       | Xác nhận sử dụng tệp mới  | Button             | —                    | —                         | Chỉ cho phép khi Media mới Ready        |
| 6       | Hủy            | Không thực hiện thay thế  | Button             | —                    | —                         | Media hiện tại tiếp tục được sử dụng    |

### **b. Quy tắc khi thay tệp**

Khi người dùng chọn **Thay thế**:

1.  Người dùng chọn file mới.

2.  Hệ thống thực hiện quy trình Upload Media của Module 1.

3.  Nếu Media mới chưa Ready:

    - chưa cập nhật dữ liệu nghiệp vụ.

4.  Nếu Media mới Ready:

    - cho phép thực hiện cập nhật.

5.  Khi cập nhật thành công:

    - entity chuyển sang sử dụng newMediaId.

6.  Media cũ được đưa vào quy trình xử lý lifecycle.

### **c. Trường hợp người dùng hủy**

Nếu người dùng chọn file mới nhưng chưa lưu thay đổi:

- oldMediaId tiếp tục được sử dụng;

- entity nghiệp vụ không thay đổi.

Nếu Media mới đã upload thành công nhưng người dùng hủy form:

- Media mới không được tự động gắn vào entity;

- Media mới được xử lý theo chính sách Media chưa được sử dụng.

### **d. Thông báo**

| **STT** | **Trường hợp**          | **Nội dung đề xuất**                             |
|---------|-------------------------|--------------------------------------------------|
| 1       | Media mới chưa sẵn sàng | Tệp mới chưa sẵn sàng để sử dụng.                |
| 2       | Media mới không hợp lệ  | Tệp mới không hợp lệ.                            |
| 3       | Sai mục đích sử dụng    | Tệp không phù hợp với loại dữ liệu cần thay thế. |
| 4       | Không có quyền          | Bạn không có quyền thay đổi tệp này.             |
| 5       | Cập nhật thất bại       | Không thể cập nhật tệp. Vui lòng thử lại.        |
| 6       | Thành công              | Cập nhật tệp thành công.                         |

## 4.7. Ràng buộc

- Không được ghi đè binary của Media hiện tại.

- Mỗi file mới phải tạo một mediaId mới.

- newMediaId phải ở trạng thái Ready trước khi được sử dụng.

- newMediaId phải phù hợp với purpose của vị trí cần thay thế.

- newMediaId phải thuộc đúng ownerService, ownerEntityType và ownerEntityId.

- MediaService không được tự chỉnh sửa database của Business Service.

- Business Service chịu trách nhiệm cập nhật business reference từ oldMediaId sang newMediaId.

- Không xóa oldMediaId trước khi việc cập nhật business reference được xác nhận thành công.

- Nếu việc cập nhật thất bại, business entity phải tiếp tục sử dụng Media cũ.

- Nếu không xác định chắc chắn kết quả cập nhật, không được cleanup Media mới hoặc Media cũ cho đến khi reconciliation hoàn tất.

- Media mới được upload nhưng không được sử dụng phải được xử lý theo chính sách orphan Media.

- URL của Media cũ hoặc mới không được sử dụng làm reference nghiệp vụ; Business Service chỉ lưu mediaId.
