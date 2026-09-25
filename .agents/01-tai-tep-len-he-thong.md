# 1. Tải tệp lên hệ thống

## 1.1. User Story

Là người dùng đang thực hiện một nghiệp vụ có sử dụng hình ảnh hoặc tài liệu, tôi muốn tải tệp lên hệ thống trước để nhận MediaId, sau đó sử dụng MediaId này khi gửi yêu cầu tạo hoặc cập nhật dữ liệu nghiệp vụ.

Là Business Service, tôi muốn kiểm tra quyền upload và cấp một phiên upload cho người dùng, để binary được tải trực tiếp lên Object Storage mà không đi qua service nghiệp vụ.

Là Business Service, tôi muốn chỉ chấp nhận MediaId đã ở trạng thái Ready khi lưu tham chiếu vào entity nghiệp vụ.

## 1.2. Đầu vào

| STT | Tên trường         | Kiểu dữ liệu               | Bắt buộc | Mô tả / ràng buộc                                                 |
|-----|--------------------|----------------------------|----------|-------------------------------------------------------------------|
| 1   | Tệp                | File                       | Có       | Tệp hình ảnh hoặc tài liệu phù hợp với chức năng đang sử dụng     |
| 2   | Mục đích sử dụng   | Lựa chọn/Hệ thống xác định | Có       | Ví dụ: ảnh thiết bị, logo tổ chức, giấy phép kinh doanh, chứng từ |
| 3   | Đối tượng liên kết | Định danh                  | Có       | Đối tượng nghiệp vụ mà tệp được sử dụng                           |
| 4   | Tên tệp            | Chuỗi ký tự                | Tự động  | Lấy từ tệp người dùng lựa chọn                                    |
| 5   | Định dạng tệp      | Chuỗi ký tự                | Tự động  | Hệ thống kiểm tra theo loại tệp được phép                         |
| 6   | Dung lượng tệp     | Số                         | Tự động  | Không được vượt giới hạn cấu hình theo mục đích sử dụng           |

## 1.3. Đầu ra

| **STT** | **Trường hợp**                              | **Kết quả hệ thống**                                                                     | **Dữ liệu / Thông báo trả về**                                           |
|---------|---------------------------------------------|------------------------------------------------------------------------------------------|--------------------------------------------------------------------------|
| 1       | Khởi tạo phiên upload thành công            | Tạo MediaAsset ở trạng thái PendingUpload và MediaUploadSession ở trạng thái Pending     | mediaId, uploadId, uploadUrl, method, requiredHeaders, expiresAtUtc      |
| 2       | Upload binary lên Object Storage thành công | File đã được lưu tạm tại Object Storage nhưng **chưa được phép sử dụng trong nghiệp vụ** | FE ghi nhận upload thành công và tiếp tục bước Complete                  |
| 3       | Complete upload thành công                  | MediaService xác minh file hợp lệ; MediaAsset → Ready, MediaUploadSession → Completed    | mediaId, status = Ready, metadata cơ bản của file                        |
| 4       | File không hợp lệ                           | Media không được sử dụng; MediaAsset → Rejected; object được đưa vào cleanup             | Thông báo lỗi tương ứng: sai định dạng, vượt dung lượng, checksum sai... |
| 5       | Chưa upload file nhưng gọi Complete         | Không chuyển Media sang Ready; session vẫn Pending nếu chưa hết hạn                      | "Tệp chưa được tải lên hoặc tải lên chưa hoàn tất."                      |
| 6       | Upload session hết hạn                      | MediaUploadSession → Expired; phiên cũ không được tiếp tục sử dụng                       | "Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp."                        |
| 7       | Retry cùng Idempotency-Key hợp lệ           | Không tạo Media mới; trả lại kết quả/session đã tạo trước đó                             | Cùng mediaId, uploadId và trạng thái hiện tại                            |
| 8       | Idempotency-Key trùng nhưng dữ liệu khác    | Không tạo upload session mới                                                             | "Yêu cầu tải tệp bị xung đột."                                           |
| 9       | Object Storage hoặc hệ thống tạm thời lỗi   | Không trả Media ở trạng thái Ready                                                       | Thông báo lỗi tạm thời, cho phép người dùng thử lại                      |
| 10      | Media đã Ready                              | FE có thể sử dụng mediaId trong request nghiệp vụ tiếp theo                              | Ví dụ imageMediaIds: \["\<mediaId\>"\]                                   |

Sau khi quá trình tải và xác minh tệp hoàn tất thành công, hệ thống trả về một MediaId ở trạng thái Ready. MediaId là định danh ổn định được Frontend sử dụng trong các yêu cầu nghiệp vụ tiếp theo. URL upload chỉ có giá trị tạm thời và không được sử dụng làm tham chiếu nghiệp vụ.

Ví dụ:

```text
{
"mediaId": "0199...abc",
"status": "Ready",
"originalFileName": "may-xuc.jpg",
"contentType": "image/jpeg",
"sizeBytes": 1250340
}
```

## 1.4. Tiêu chí chấp nhận

**AC1:** Business Service gửi thông tin hợp lệ thì hệ thống tạo một MediaAsset và MediaUploadSession mới.

**AC2:** Response tạo upload phải có MediaId, UploadId, upload instruction và thời gian hết hạn.

**AC3:** Binary được upload trực tiếp từ Browser đến Object Storage, không truyền qua MediaService.

**AC4:** Chưa upload object mà gọi Complete thì Media không được chuyển sang Ready.

**AC5:** Object có dung lượng thực tế, định dạng hoặc file signature không phù hợp policy thì Media bị từ chối và không trở thành Ready.

**AC6:** Chỉ khi Complete thành công Business Service mới được sử dụng MediaId cho entity nghiệp vụ.

**AC7:** Gọi Complete lại cho một session đã Completed phải trả cùng kết quả, không tạo Media mới.

**AC8:** Session đã Expired không được hồi sinh hoặc cấp lại URL upload. Muốn upload lại phải tạo request mới với Idempotency-Key mới.

**AC9:** Cùng caller + cùng Idempotency-Key + cùng request phải trả cùng upload session; cùng key nhưng nội dung khác phải bị từ chối.

**AC10:** Client không được nhìn thấy Bucket, ObjectKey hoặc storage credential nội bộ.

## 1.5. Nguyên tắc hoạt động

**1.5.1. Nguyên tắc tải tệp**

Người dùng thực hiện tải tệp tại chức năng nghiệp vụ tương ứng.

Trước khi cho phép tải, hệ thống nghiệp vụ phải kiểm tra người dùng có quyền thao tác trên đối tượng liên quan.

Tệp được tải trực tiếp lên hệ thống lưu trữ tệp.

Sau khi tải thành công, hệ thống tiến hành kiểm tra tệp trước khi cho phép sử dụng.

Tệp chỉ được sử dụng khi trạng thái là Ready.

**1.5.2. Nguyên tắc tham chiếu Media**

Mỗi tệp tải thành công được hệ thống cấp một MediaId duy nhất.

Các service nghiệp vụ lưu MediaId thay vì lưu binary hoặc đường dẫn lưu trữ.

Một MediaId đại diện cho một Media cụ thể và không được tái sử dụng cho nội dung tệp khác.

Khi tạo hoặc cập nhật dữ liệu nghiệp vụ, Frontend gửi MediaId tương ứng cho Business Service.

Ví dụ:

> Người dùng tải ba ảnh thiết bị → hệ thống trả ba MediaId → khi tạo thiết bị, Frontend gửi ba MediaId trong thông tin thiết bị.

**1.5.3. Nguyên tắc liên kết với đối tượng nghiệp vụ**

Media phải xác định được đối tượng nghiệp vụ mà tệp dự kiến được sử dụng.

Mỗi Media trong MVP gắn với:

- Service sở hữu;

- Loại đối tượng;

- ID đối tượng.

Nếu đối tượng nghiệp vụ chưa được lưu, hệ thống có thể tạo ID của đối tượng trước khi thực hiện tải tệp.

Business Service chịu trách nhiệm kiểm tra MediaId có đúng đối tượng và đúng mục đích sử dụng trước khi lưu tham chiếu.

**1.5.4. Nguyên tắc kiểm tra tệp**

- Hệ thống kiểm tra tệp theo chính sách của từng mục đích sử dụng.

- Việc kiểm tra tối thiểu bao gồm:

  - định dạng;

  - dung lượng;

  - loại nội dung;

  - tính hợp lệ cơ bản của file.

- Không chỉ dựa vào phần mở rộng của tên file để xác định loại file.

- Tệp không đạt yêu cầu bị từ chối và không được sử dụng trong nghiệp vụ.

**1.5.5. Nguyên tắc trạng thái tải tệp**

Quá trình tải tệp có các trạng thái chính:

- PendingUpload: đang chờ hoặc đang thực hiện tải tệp.

- Ready: tệp đã tải và kiểm tra thành công.

- Rejected: tệp không đáp ứng yêu cầu.

- Deleted: tệp đã bị xóa về mặt logic.

Chỉ Media ở trạng thái Ready được phép liên kết mới với dữ liệu nghiệp vụ.

**1.5.6. Nguyên tắc tải lại**

- Nếu quá trình tải bị lỗi trước khi hoàn tất, người dùng có thể thực hiện tải lại.

- Nếu phiên tải đã hết hạn, phiên cũ không được tiếp tục sử dụng.

- Việc tải lại sau khi phiên hết hạn tạo một phiên tải mới.

- Hệ thống phải tránh tạo nhiều Media do cùng một yêu cầu bị gửi lại ngoài ý muốn.

**1.5.7. Nguyên tắc tệp chưa được sử dụng**

- Tệp có thể được tải lên trước khi người dùng hoàn tất thao tác nghiệp vụ.

- Nếu người dùng không hoàn tất nghiệp vụ hoặc dữ liệu nghiệp vụ không được lưu, Media có thể không có tham chiếu thực tế.

- Các Media không còn được sử dụng phải được xác định và dọn dẹp theo chính sách vòng đời của hệ thống.

- Không xóa ngay một Media nếu hệ thống chưa xác định chắc chắn Media không còn được sử dụng.

## 1.6. Mô tả yêu cầu

### **1.6.1 Tải tệp**

| **STT** | **Tên**        | **Mô tả**                              | **Control**         | **Giá trị mặc định** | **Bắt buộc**                  | **Quy tắc**                                         |
|---------|----------------|----------------------------------------|---------------------|----------------------|-------------------------------|-----------------------------------------------------|
| 1       | Chọn tệp       | Chọn hình ảnh/tài liệu cần tải         | File Upload         | Trống                | Có khi chức năng yêu cầu file | Chỉ cho phép loại file phù hợp với mục đích sử dụng |
| 2       | Tên tệp        | Hiển thị tên tệp đã chọn               | Text                | Tên tệp được chọn    | —                             | Không cho sửa nếu UI không có nhu cầu đổi tên       |
| 3       | Dung lượng     | Hiển thị dung lượng tệp                | Text                | Tự động              | —                             | Vượt giới hạn thì báo lỗi                           |
| 4       | Trạng thái tải | Hiển thị trạng thái hiện tại           | Label/Status        | Chưa tải             | —                             | Đang tải / Đang xử lý / Hoàn tất / Lỗi              |
| 5       | Tiến trình tải | Hiển thị tiến trình                    | Progress Bar        | 0%                   | —                             | Hiển thị khi đang tải                               |
| 6       | Thử lại        | Tải lại khi có lỗi                     | Button              | Ẩn                   | —                             | Chỉ hiển thị khi tải thất bại                       |
| 7       | Xóa tệp        | Bỏ tệp khỏi dữ liệu đang nhập          | Icon/Button         | —                    | —                             | Bỏ Media khỏi form hiện tại                         |
| 8       | MediaId        | Định danh Media sau khi tải thành công | Hidden/System Value | Trống                | —                             | Frontend giữ để gửi trong request nghiệp vụ         |

#### 

### **1.6.2 Quy tắc tải tệp**

| **STT** | **Trường hợp**    | **Nội dung**                                    |
|---------|-------------------|-------------------------------------------------|
| 1       | Chưa chọn tệp     | Vui lòng chọn tệp cần tải.                      |
| 2       | Tệp rỗng          | Tệp không có nội dung.                          |
| 3       | Sai định dạng     | Định dạng tệp không được hỗ trợ.                |
| 4       | Vượt dung lượng   | Dung lượng tệp vượt quá giới hạn cho phép.      |
| 5       | Tải tệp thất bại  | Không thể tải tệp lên. Vui lòng thử lại.        |
| 6       | Phiên tải hết hạn | Phiên tải tệp đã hết hạn. Vui lòng tải lại tệp. |
| 7       | Tệp không hợp lệ  | Tệp không hợp lệ hoặc không đáp ứng yêu cầu.    |
| 8       | Thành công        | Tải tệp thành công.                             |

**1.6.3 Sử dụng Media sau khi tải**

Sau khi Media ở trạng thái Ready:

- Frontend lưu tạm MediaId trong dữ liệu của form.

- Khi người dùng nhấn **Lưu / Tạo mới / Cập nhật** ở chức năng nghiệp vụ, Frontend gửi MediaId cùng các trường dữ liệu khác.

- Business Service kiểm tra MediaId hợp lệ trước khi lưu.

- Business Service không nhận lại binary của tệp trong request nghiệp vụ.

Ví dụ trên form thiết bị:

Ảnh thiết bị

\[ may-xuc-1.jpg ✓ \]

\[ may-xuc-2.jpg ✓ \]

→ MediaId A

→ MediaId B

Người dùng nhấn "Tạo thiết bị"

Request nghiệp vụ chứa:

```text
{
"name": "Máy xúc Komatsu",
"imageMediaIds": [
"media-id-A",
"media-id-B"
]
}
```

## 1.7. Ràng buộc

- Chỉ các định dạng tệp nằm trong danh sách cho phép của từng mục đích sử dụng mới được chấp nhận.

- Dung lượng tối đa của tệp được cấu hình theo từng mục đích sử dụng.

- Tệp không hợp lệ không được sử dụng trong dữ liệu nghiệp vụ.

- Chỉ Media có trạng thái Ready được phép tạo tham chiếu mới.

- Mỗi Media được định danh bằng một MediaId duy nhất.

- Các service nghiệp vụ chỉ lưu MediaId, không lưu binary file hoặc đường dẫn lưu trữ nội bộ.

- Tên file người dùng cung cấp không được dùng trực tiếp làm khóa lưu trữ.

- Không cho phép người dùng tự xác định Service sở hữu Media.

- Không để lộ thông tin nội bộ của Object Storage cho người dùng.

- Phiên tải tệp có thời hạn; phiên hết hạn không được tiếp tục sử dụng.

- Media không còn được sử dụng phải được xử lý theo chính sách vòng đời và dọn dẹp của hệ thống.

- Việc kiểm tra định dạng tệp không được xem là cơ chế kiểm tra mã độc.

- Chức năng quét malware không thuộc phạm vi MVP.
