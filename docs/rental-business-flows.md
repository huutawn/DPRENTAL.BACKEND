# Các luồng chức năng RentalService xử lý

Tài liệu đi theo hành động nghiệp vụ của Renter, Supplier, Admin và hệ thống. Kafka, HTTP và SignalR được giải thích tại bước tham gia vào nghiệp vụ. Bản đồ kỹ thuật bổ trợ: [source walkthrough](rental-source-walkthrough.md). Quy tắc nền: [Project Guide](PROJECT_GUIDE.md).

## 1. Rental quản lý những gì?

Rental quản lý giỏ thuê, đơn thuê, các dòng thuê, quá trình xác nhận/bàn giao/trả, tranh chấp, khấu trừ và trạng thái hoàn tiền liên quan đơn thuê. Inventory quyết định thiết bị vật lý nào được cấp phát; Payment thực hiện thanh toán/hoàn tiền; Media giữ bằng chứng; Auth cung cấp danh tính.

Một Order có thể chứa nhiều Supplier. SubOrder gom các item của cùng Supplier. Một RentalItem là listing + số lượng + khoảng thuê; một Assignment là một máy vật lý được Inventory cấp cho item. Vì vậy phải phân biệt trạng thái Order, item và từng máy; một máy được trả không có nghĩa cả Order đã hoàn tất.

## 2. Renter chuẩn bị đơn thuê

### Quản lý thông tin nhận hàng và nhận hoàn tiền

Renter quản lý địa chỉ và tài khoản ngân hàng qua RenterAddressesController và RenterBankAccountsController. Các service [Addresses](../RentalService/src/Application/Services/Addresses) và [BankAccounts](../RentalService/src/Application/Services/BankAccounts) xử lý dữ liệu thuộc người dùng. Thông tin được chọn khi checkout trở thành snapshot của đơn; không xem việc sửa danh bạ hiện tại là sửa ngược lịch sử đơn.

### Thêm, sửa, xóa và xem giỏ

1. Renter chọn listing, số lượng và khoảng thuê.
2. Rental kiểm tra dữ liệu và đánh giá listing/availability qua Inventory.
3. Rental lưu dòng giỏ và trả kết quả đánh giá cho màn hình.
4. Khi xem lại hoặc checkout phải đánh giá lại; giỏ không giữ máy và không khóa giá.

Source: RentalCartController → [RentalCartUseCases, GetRentalCart, RentalCartItemEvaluator](../RentalService/src/Application/Services/Cart). Lỗi cần phân biệt: dữ liệu không hợp lệ, listing không còn phù hợp và thiếu khả dụng. Thêm vào giỏ thành công không phải cam kết checkout sẽ thành công.

## 3. Renter checkout và thanh toán

1. Renter gửi lựa chọn thuê, thông tin giao nhận, chấp nhận điều khoản và idempotency key.
2. Rental xác thực chủ thể, kiểm tra payload, snapshot giá và availability hiện tại.
3. Điều phối giữ đủ máy cho mọi dòng. Một dòng không giữ được thì không tạo đơn có thể thanh toán một phần; cần giải phóng những hold đã thành công.
4. Khi đủ điều kiện, lưu Order/items/assignments/snapshots, operation receipt và event trong transaction.
5. Commit trước khi gọi Payment tạo invoice. Việc tạo invoice lỗi tạm thời có thể được recovery trong phần thời gian hold còn lại.
6. Renter chỉ sang thanh toán khi readyForPayment=true. Operation Succeeded chỉ nói rằng order đã được tạo.
7. Payment thông báo InvoicePaid qua Kafka. Rental kiểm tra message và xử lý payment workflow; cập nhật nghiệp vụ cùng inbox/outbox theo transaction.
8. Thanh toán được chấp nhận thì đi tiếp tới chờ Supplier xác nhận. Thanh toán đến muộn hoặc đến sau hủy/hết hạn phải xét recovery/refund, không ép mọi trường hợp thành Paid.

Source: RentalCheckoutController → [Checkout](../RentalService/src/Application/Services/Checkout) → [PaymentWorkflowService](../RentalService/src/Application/Services/Payments/PaymentWorkflowService.cs). Đọc thêm [deadline và payment đến muộn](rental-checkout-payment-deadline.md).

Hold và PaymentDeadline dùng cùng mốc; tạo invoice không gia hạn. Một renter có thể có nhiều order PendingPayment độc lập, nhưng checkout đang xử lý phải được kiểm soát tranh chấp. Retry giữ nguyên key và payload; tái sử dụng key khác payload là conflict.

## 4. Supplier nhận và quyết định đơn

1. Supplier xem danh sách/detail SubOrder thuộc mình.
2. Chấp nhận hoặc từ chối trong điều kiện trạng thái và deadline cho phép.
3. Khi chấp nhận, Rental điều phối xác nhận allocation với Inventory; không chỉ cập nhật một field trạng thái.
4. Khi từ chối, Rental xử lý trạng thái item/suborder, giải phóng allocation và điều phối hoàn tiền liên quan.
5. Order được rollup từ các phần bên trong; không suy ra mọi item cùng trạng thái chỉ từ Order.

Source: SupplierSubOrdersController → [SupplierDecision](../RentalService/src/Application/Services/SupplierDecision) và [Operations](../RentalService/src/Application/Services/Operations). HTTP accepted/operation pending chưa chứng minh Inventory đã hoàn tất. Theo tiếp result handler và receipt khi debug.

## 5. Bàn giao và xác nhận đã nhận máy

1. Supplier gửi bàn giao cho item AwaitingHandover, đủ các assignment và bằng chứng theo yêu cầu.
2. Rental kiểm tra quyền sở hữu, thời điểm, evidence qua Media và chống duplicate.
3. Rental điều phối Inventory xác nhận bàn giao. Khi còn pending, chưa coi máy đã bắt đầu thuê.
4. Sau xác nhận thành công, có HandoverRecord và item InRental.
5. Renter có thể acknowledge đã nhận. Đây là xác nhận bổ sung, không chuyển InRental lần nữa; replay giữ xác nhận đầu tiên.

Nếu chưa được giao, renter có nhánh cancel-not-delivered từ mốc D+1 theo giờ Việt Nam. Bàn giao đang pending/đối soát có thể chặn hủy để tránh vừa bàn giao vừa hoàn tiền.

Source: RentalItemsController, [SupplierDecision](../RentalService/src/Application/Services/SupplierDecision), [Handover](../RentalService/src/Application/Services/Handover), [Cancellation](../RentalService/src/Application/Services/Cancellation). Điều kiện/payload chi tiết: [handover guide](rental-handover-return-fe-guide.md#5-task-1--bàn-giao-và-xác-nhận-nhận).

## 6. Đang thuê và khai báo trả máy

1. Máy đã giao tiếp tục được theo dõi theo assignment.
2. Renter chọn một hoặc nhiều máy đủ điều kiện và khai báo đã trả.
3. Rental tạo ReturnRequest; unit đang có yêu cầu mở không được tạo yêu cầu chồng.
4. Item vẫn InRental. Chưa tạo ReturnRecord, chưa giải phóng Inventory, chưa tự hoàn cọc.
5. Các máy khác chưa trả tiếp tục thuê; yêu cầu trả có deadline xác nhận riêng.

Source: RentalReturnsController → [ReturnService.Requests](../RentalService/src/Application/Services/Returns/ReturnService.Requests.cs). Không dùng số unit trong ReturnRequest làm số máy đã được Supplier thực nhận.

## 7. Supplier thu hồi và kiểm tra máy

1. Supplier chọn các máy đã giao và chưa ghi nhận trả; có thể thu hồi từng phần.
2. Nhập thời điểm thực nhận và inspection từng máy: NORMAL hoặc DAMAGED.
3. DAMAGED cần mô tả và ảnh hợp lệ; bằng chứng phải đáp ứng điều kiện Media.
4. Rental ghi ReturnRecord, cập nhật assignment và đóng request unit liên quan.
5. Rental phát command Inventory xử lý trả: máy bình thường đóng allocation; máy hỏng còn cần disposition chặn đặt mới.
6. Khi Inventory trả kết quả, Rental cập nhật xác nhận đóng allocation. ClosedAt của request unit chỉ chứng minh Rental đã ghi nhận thực nhận.
7. Còn máy chưa nhận thì item tiếp tục InRental. Nhận đủ đi vào PendingSettlement; chưa tự coi Completed.

Source: [ReturnService](../RentalService/src/Application/Services/Returns/ReturnService.cs) và [InventoryReturnResultHandler](../RentalService/src/Application/Kafka/Consumers/InventoryReturnResultHandler.cs). Toàn bộ NORMAL hoặc không có cọc có thể chốt khấu trừ 0; có damage và cọc thì mở nhánh khấu trừ theo điều kiện. Xem [return guide](rental-handover-return-fe-guide.md#7-task-3--a-kiểm-tra-và-ghi-nhận-thực-nhận).

## 8. Bất đồng về việc trả và Admin xử lý

1. Renter/Supplier báo vấn đề trên request unit còn mở; Rental lưu issue và lịch sử người báo.
2. Quá 24h từ SubmittedAt mà còn unit actionable thì có cảnh báo overdue. Không dùng ActualReturnAt do renter khai để tính deadline.
3. Hết hạn không tự xác nhận đã thu hồi, không tạo ReturnRecord và không tự hoàn tiền.
4. Admin xem danh sách/detail, chọn unit và quyết định ConfirmReceived hoặc RejectRequest.
5. ConfirmReceived dùng workflow thu hồi và inspection; ghi Admin là actor thực hiện.
6. RejectRequest đóng nhánh yêu cầu bằng RejectedAt, không gọi Inventory. Nếu máy vẫn đủ điều kiện, renter có thể khai báo trả lại bằng yêu cầu mới.
7. Một request có thể được xử lý từng phần và kết quả hỗn hợp; lịch sử issue/resolution được giữ lại.

Source: AdminReturnRequestsController, [ReturnService.Issues](../RentalService/src/Application/Services/Returns/ReturnService.Issues.cs), [ReturnService.Admin](../RentalService/src/Application/Services/Returns/ReturnService.Admin.cs). Quyền Admin không được thay bằng service token; ghi chú quản trị không được lộ nguyên trạng cho renter/supplier.

## 9. Khấu trừ, tranh chấp tài chính và hoàn tiền

Sau thu hồi đủ máy, Rental xét damage, cọc và deadline để đi tiếp settlement. Supplier đề xuất khấu trừ; renter phản hồi theo điều kiện của đề xuất. Nhánh không thống nhất được xử lý qua dispute/admin, thay vì Supplier tự quyết định mọi số tiền.

Các entrypoint là RentalDeductionsController và RentalDisputesController; đọc [DeductionService, DeductionResponseService và partial Admin](../RentalService/src/Application/Services/Settlement). Phải lần tiếp Operations/Settlement và các consumer RefundCompleted/RentalSettled để phân biệt: đã yêu cầu tất toán, đã được Payment xác nhận và đã hoàn tất nghiệp vụ.

AdminRefundsController và [Refund services](../RentalService/src/Application/Services/Refunds) phục vụ quản trị hoàn tiền. Response chấp nhận hủy hoặc tạo refund không chứng minh người thuê đã nhận tiền. Không ghi lại công thức khấu trừ phỏng đoán; công thức/điều kiện cụ thể cần đọc method tương ứng trước khi thay đổi.

## 10. Tra cứu, lịch và thống kê

| Người dùng | Chức năng | Source |
| --- | --- | --- |
| Renter | Xem danh sách/detail order, ghi chú giao nhận | RentalOrdersController và Services/Orders |
| Supplier | Xem suborders theo phạm vi sở hữu | SupplierSubOrdersController và SupplierSubOrderQueryService |
| Renter | Xem lịch thuê | RenterCalendarController và Services/Calendar |
| Admin | Tra cứu order, return requests, refund và thống kê supplier | Các Admin controllers; Services/Orders, Refunds, SupplierStatistics |

Đây là luồng đọc/projection; không làm thay đổi trạng thái thuê chỉ vì mở màn hình. Dữ liệu và trường được trả phải theo quyền người xem; không dùng Admin response làm response public.

## 11. Các luồng hệ thống tự chạy

- Checkout timeout: xử lý operation/hold hết thời gian.
- Payment expiry: hết hạn thanh toán, điều phối hủy invoice và giải phóng allocation.
- Supplier confirmation expiry: xử lý quá hạn quyết định của Supplier.
- Handover overdue/expiry: cảnh báo rồi xử lý quá hạn bàn giao theo grace cấu hình.
- Overdue protection: điều phối bảo vệ Inventory khi máy chưa được trả đúng hạn.
- Return confirmation overdue: cảnh báo yêu cầu trả chưa được xác nhận, không tự thu hồi.
- Deduction deadline: xử lý nhánh khấu trừ hết hạn.
- Operation recovery: tiếp tục các side effect có receipt chưa hoàn tất.

Điểm tìm worker được đăng ký tại [Application DI](../RentalService/src/Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs). Sau đó lần service được worker gọi; không coi các loại expiry có cùng điều kiện hoặc cùng hậu quả tài chính.

## 12. Kafka và hub đứng ở đâu trong nghiệp vụ?

Kafka mang command/result giữa Rental và Inventory, nhận Payment events và phát Rental events. Outbox bảo đảm ý định phát đi cùng dữ liệu đã commit; Inbox và idempotency chống xử lý lặp. Broker đã nhận không đồng nghĩa nghiệp vụ service đích đã thành công.

Hub báo dữ liệu hiển thị đã thay đổi sau mutation. Ví dụ tạo order → ORDER_CREATED, Payment xác nhận → PAYMENT_CONFIRMED, gửi khai báo trả → RETURN_REQUESTED, ghi nhận thu hồi → RETURN_SUBMITTED, Inventory xác nhận → RETURN_RECORDED. Client nhận tín hiệu rồi đọc lại REST. Hub không phải nơi thực thi checkout, handover hoặc return.

Chi tiết cấu hình và đường gọi nằm ở [source walkthrough](rental-source-walkthrough.md) và [realtime contract](rental-realtime-client-integration.md).

## Giới hạn đối chiếu

Đây là bản giải thích chức năng từ source đã khảo sát và các tài liệu contract/nghiệp vụ hiện có. Chưa chạy end-to-end hoặc kiểm chứng độc lập mọi điều kiện trong từng service. Các phần settlement, thống kê và quản trị ở mức định hướng chức năng; không coi đây là audit đầy đủ công thức, quyền và mọi nhánh lỗi của chúng.
