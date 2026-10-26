# Kế hoạch SignalR realtime foundation cho Rental

Ngày khảo sát: 2026-10-26. Trạng thái: kế hoạch, chưa triển khai tính năng.

Baseline Rental: `f4f1b7f`. Tham chiếu Payment: `2300963d` trên `payment/develop` local.
Payment working tree đang có nhiều tracked files bị xóa; khảo sát bằng `git show HEAD:<path>` và `git grep HEAD`, không restore hoặc sửa repository đó.
Các kết luận về Payment áp dụng cho snapshot này, không khẳng định đây là revision mới nhất trên remote.

## 1. Kết luận và phạm vi điều chỉnh

Yêu cầu phù hợp với Rental: dùng SignalR để invalidation, REST tiếp tục sở hữu dữ liệu và kiểm tra quyền.
Triển khai theo pattern Hub + notifier + DI + gửi sau commit của Payment, có điều chỉnh theo ranh giới kiến trúc Rental.
Foundation dùng best-effort notification sau commit; không thêm durable delivery/replay, không đổi Kafka outbox.
Đây là lựa chọn phạm vi có giới hạn: lỗi push hoặc crash sau commit có thể làm mất notification; reconnect/resume phải GET lại.
Không cam kết client luôn cập nhật khi socket vẫn mở nhưng một lần gửi đã thất bại. Nếu cần đảm bảo đó, phải có task durable delivery riêng.

### Đối chiếu yêu cầu với code hiện tại

| Nội dung | Hiện trạng và quyết định |
| --- | --- |
| Bốn GET renter/supplier | Đã có. Giữ endpoint, pagination, filter và ownership; không tạo API đọc thay thế. |
| Hub `/api/v1/rental/hubs/orders` | Hợp lý. Chưa thấy Hub Rental trong khảo sát. Map explicit từ composition root; không phụ thuộc MVC convention. |
| Hai detail chưa expose version | Không đúng với baseline: renter detail đã có `version`, supplier detail đã có `orderVersion`. Giữ nguyên hai field này. |
| Bổ sung version ở list | Cần làm: renter summary thêm `version`, supplier summary thêm `orderVersion`; lấy từ parent `RentalOrder.Version`, không thêm counter SubOrder. |
| Query token cho browser | JWT Rental chưa đọc query. Thêm `OnMessageReceived` có phạm vi chính xác Hub và endpoint negotiate; không mở cho REST hoặc path cùng prefix giả. |
| Human user và service token | Tái sử dụng `RentalCallerContext`; policy Hub phải loại `u=svc` và yêu cầu UserId GUID khác empty. Không tái sử dụng policy `Renter` hiện tại mà chưa kiểm tra semantics. |
| Nhóm theo UserId | Giữ `renter:{UserId:D}` và `supplier:{UserId:D}`, chỉ server join. Một user có thể đồng thời đóng hai vai trò. |
| Supplier chỉ nhận phần của mình | Recipient lấy từ SubOrder lưu trong aggregate. Không broadcast cho mọi supplier cùng Order khi chỉ một SubOrder thay đổi. |
| Supplier trước thanh toán | Repository hiện chỉ cho supplier đọc đơn đã thanh toán. Notification phải theo cùng điều kiện eligibility, không lộ đơn PendingPayment. |
| Chỉ thông báo khi status đổi | Chưa đủ: REST còn thay đổi `invoiceRef`, delivery note, handover evidence, return, refund, deduction/dispute. Phải bao phủ thay đổi có thể quan sát qua bốn GET dù parent status giữ nguyên. |
| Xác nhận supplier/handover | Có command/receipt và Inventory reply bất đồng bộ. Không gọi một command được tiếp nhận là `SUPPLIER_CONFIRMED` hoặc `HANDOVER_CONFIRMED`. |
| Internal API key | Rental có middleware yêu cầu `X-Api-Key`. Browser không được biết key; phải chứng minh Gateway inject key cả negotiate và WebSocket upgrade. |
| Gateway | Local config hiện rewrite `/api/v1/rental/{everything}` thành `/rental/{everything}`. Hub cần hai route explicit và giữ nguyên downstream path. Đây là khác biệt cần xử lý trước nghiệm thu public URL. |
| Reconnect/resume | Giữ yêu cầu GET lại, không replay. Initial connect cũng GET lại để đóng khoảng trống lúc thiết lập kết nối. |
| Renter không nhận physical information | SignalR không chứa AssetId/AllocationId/serial/evidence. Giữ coverage REST renter hiện đã chặn asset/allocation. |
| Không chứa PII | Chỉ có các ID tài nguyên được phép và metadata invalidation; không thêm user IDs, tên, điện thoại, địa chỉ, tài khoản ngân hàng, note hoặc payload nội bộ. |

### Nguồn code Rental đã khảo sát

- `RentalService/src/Api/Program.cs`: DI, policy, middleware, MVC prefix, endpoint mapping.
- `RentalService/src/Api/Security/JwtAuthenticationExtensions.cs`: JWT/JWKS và validation.
- `RentalService/src/Api/Security/InternalApiKeyMiddleware.cs`: ràng buộc API key nội bộ.
- `RentalService/src/Application/Security/RentalCallerContext.cs`, `RentalAuthorizationPolicy.cs`: identity và quyền.
- `RentalService/src/Application/DTOs/Orders/OrderQueryDtos.cs`: bốn response contracts.
- `RentalService/src/Application/Services/Orders/{RenterOrderQueryService,SupplierSubOrderQueryService,RenterDeliveryNoteService}.cs`.
- `RentalService/src/Domain/IRepositories/OrderQueryRecords.cs`, `Repositories/RentalOrderRepository.cs`: list projections và supplier eligibility.
- `RentalService/src/Domain/Entities/RentalOrder.cs`: Version, AttachInvoice, read-model updates.
- `RentalService/src/Application/Kafka/Consumers/{PaymentEventsDispatcher,InventoryAllocationResultHandler,InventoryReturnResultHandler}.cs`: staged handlers và transaction bên ngoài.
- `RentalService/src/Application/Services/{Checkout,Payments,Operations,Cancellation,Expiry,Overdue,Returns,Settlement,Integration}`: mutation owners tìm qua graph callers của `RecordReadModelUpdate` và `AttachInvoice`.
- `RentalService/src/Application/Kafka/Workers/OutboxPublisherWorker.cs`, `Domain/Repositories/OutboxRepository.cs`: outbox hiện chuyên Kafka, không phải dispatcher SignalR.
- Tests hiện có: `OrderQueryApiTests`, `OrderQueryServiceTests`, `OrderWorkflowAndVersionTests`, `VersionPersistenceRegressionTests`, `RentalCartJwtApiTests`, các workflow/integration tests tương ứng.

## 2. Áp dụng tham chiếu PaymentService

Các đường dẫn dưới đây là path trong Git tree Payment ở revision nêu trên.

| Pattern Payment | Áp dụng vào Rental |
| --- | --- |
| `Application/Hub/PaymentHub.cs`, policy riêng, không có public join method | Giữ Hub mỏng và policy riêng, nhưng đặt transport implementation ở `Api` theo quy tắc Rental. |
| `IPaymentHubNotifier` + `PaymentHubNotifier` dùng `IHubContext` | Giữ abstraction phía Application và implementation SignalR phía Api; contract chỉ có hai kiểu invalidation thực tế, không copy generic gửi tùy ý. |
| `Extensions/PaymentHubServiceCollectionExtensions.cs` và endpoint extension | Giữ registration/mapping tập trung. Không gọi `BuildServiceProvider()` trong registration như Payment đang làm; cấu hình qua options/DI chuẩn. |
| WebSockets-only và `CloseOnAuthenticationExpiration = true` | Dùng cho foundation Rental để thống nhất cách vận hành; chưa hỗ trợ SSE/LongPolling. Client vẫn negotiate bình thường trừ khi có cấu hình thống nhất khác. |
| `Security/PaymentUserIdProvider.cs` | Chuẩn hóa GUID qua caller context hiện hữu; không xây parser claims thứ hai. Chỉ thêm IUserIdProvider nếu thực sự dùng UserIdentifier/user routing. |
| `Security/PaymentHubOriginGuardMiddleware.cs` | Giữ kiểm soát Origin cho WebSocket; CORS chỉ được thêm nếu frontend khác origin. Test allowlist, missing Origin và malformed/multiple Origin rõ ràng. |
| Payment JWT đọc cookie `accessToken` tại Hub/invoice reads | Không copy cookie fallback sang REST Rental. Task Rental dùng bearer header và query token giới hạn Hub. |
| `PaymentHubEnvelope<T>.Version = 1`, payload có `Revision` | Đây là schema version, khác aggregate version. Rental giữ flat payload đã đề xuất; `version`/`orderVersion` luôn là `RentalOrder.Version`. |
| `PaymentHubGroups` giữ registry connection/group trong memory và generic quản trị membership | Rental chỉ cần hai group cố định theo user. Dùng SignalR group manager, không copy registry/quản trị dynamic group. |
| `InvoicePaymentProcessor`, `InvoiceLifecycleService`, `InvoiceCreationService`, `TransactionResolutionService` gọi notifier sau commit | Áp dụng thời điểm sau commit. Rental còn có staged consumer nên phải gửi tại owner của transaction ngoài cùng. |
| Notifier trả `Result`, một số caller không xử lý kết quả | Rental phải xử lý/log lỗi có cấu trúc; không rollback hoặc trả business failure cho mutation đã commit chỉ vì push thất bại. |

Không tạo project/package shared giữa Payment và Rental cho task này; không phụ thuộc assembly/domain model Payment.
Không sửa các hạn chế Payment như một phần của task Rental.

## 3. Thiết kế chốt cho foundation

### Payload và định danh

Event names: `RenterOrderChanged`, `SupplierSubOrderChanged`.

```json
{"eventId":"uuid","orderId":"uuid","version":18,"changeType":"SUPPLIER_CONFIRMED","occurredAt":"2026-10-19T04:00:00Z"}
```

```json
{"eventId":"uuid","orderId":"uuid","subOrderId":"uuid","orderVersion":18,"changeType":"HANDOVER_CONFIRMED","occurredAt":"2026-10-19T04:00:00Z"}
```

- DTO riêng cho hai payload, whitelist field; không serialize entity hoặc lấy nguyên Kafka payload để gửi.
- `eventId` tạo một lần cho mỗi notification; reuse nếu retry chính notification đó. Không hứa exactly-once.
- `occurredAt` là thời điểm thay đổi nghiệp vụ UTC, không lấy thời điểm client nhận.
- Version lấy sau mutation của application flow, tương ứng dữ liệu đã commit. Không tăng version chỉ vì gửi SignalR.
- Có thể có nhiều notification cùng aggregate version. Client không được dùng một global `lastVersion` để bỏ event cho mọi SubOrder.
- `changeType` là tập hữu hạn tên công khai; không chuyển error message, reason free text hoặc tên receipt nội bộ thành changeType.
- Các tên dự kiến: `ORDER_CREATED`, `INVOICE_ATTACHED`, `PAYMENT_CONFIRMED`, `SUPPLIER_CONFIRMED`, `SUPPLIER_REJECTED`, `HANDOVER_CONFIRMED`, `ORDER_CANCELLED`, `ITEM_CANCELLED`, `PAYMENT_EXPIRED`, `SUPPLIER_CONFIRMATION_EXPIRED`, `HANDOVER_OVERDUE`, `HANDOVER_EXPIRED`, `RETURN_RECORDED`, `SETTLEMENT_CHANGED`, `REFUND_CHANGED`, `DELIVERY_NOTE_UPDATED`.
- Mỗi tên chỉ bật khi tồn tại observable change tương ứng. Không tạo state transition mới để phục vụ event.

### Ranh giới implementation

```text
Application mutation / staged handler
  -> giữ mô tả invalidation tối thiểu, đúng recipient và scope
  -> owner SaveChanges + commit transaction ngoài cùng
  -> Application notifier abstraction
  -> Api implementation / IHubContext
  -> renter group hoặc đúng supplier group
```

- Controller và background worker chỉ dispatch; application owner chọn recipient/changeType và quyết định khi nào được publish.
- Hub chỉ xác thực connection, join hai group từ danh tính đã kiểm tra; không query database hoặc quyết định workflow.
- Không publish từ repository, Entity, EF interceptor hoặc khi `SaveChanges` vẫn nằm trong outer transaction chưa commit.
- Với staged handler: trả thông tin thay đổi cần publish về application owner transaction; cập nhật tất cả callers và test doubles cùng phase. Không tạo transaction framework hoặc buffer toàn cục.
- Lấy snapshot payload/recipient của mutation đang commit, tránh đọc lại aggregate đã sang version khác rồi gắn sai event.
- Gửi có timeout hữu hạn, CancellationToken, không fire-and-forget. Batch nhiều supplier phải có giới hạn/concurrency rõ ràng.
- Business commit thành công vẫn thành công khi push lỗi. Log metadata chẩn đoán, không log token/payload nhạy cảm. Không retry business mutation để retry push.
- Nếu caller đã cancel sau commit, notification có thể mất theo best-effort contract; không giữ request/background task vô hạn.
- Enqueue command/receipt đơn thuần không phát event xác nhận. Nếu bốn GET thực sự lộ thay đổi pending thì dùng changeType tương ứng, không giả báo đã hoàn tất.

### Authentication, routing và hosting

- Policy áp dụng tại endpoint để anonymous bị 401, JWT hợp lệ nhưng service/không có human identity bị 403 trước upgrade.
- JWT sai chữ ký, expired, issuer/audience sai phải 401. Service token sai audience cũng có thể 401 tại authentication; AC 403 áp dụng token đã authenticate hợp lệ nhưng bị cấm bởi Hub policy.
- Query token chỉ được đọc khi không có bearer header và path đúng Hub hoặc `/negotiate`. Không dùng raw `StartsWith` nhận cả `/orders-evil` hoặc wildcard descendant.
- Có bearer header và query đồng thời: header ưu tiên, header sai không được fallback sang query hợp lệ.
- Token hết hạn đóng connection; client refresh token và reconnect. Không sao chép API authentication refresh chỉ có ở .NET mới hơn target `net10.0`.
- Không tạo public Subscribe/Join/Send method. Group name chuẩn hóa GUID một nơi, không phân biệt uppercase/lowercase của chuỗi JWT.
- Join group lỗi phải làm connection thất bại/abort và được ghi nhận; không để client tưởng đã subscribe thành công.
- Gateway giữ nguyên path, forward query cần thiết, hỗ trợ WebSocket upgrade, không log access_token.
- Không đưa `X-Api-Key` vào query/cookie/frontend. Giữ middleware Rental; Gateway phải cấp key cho cả hai request types hoặc có thiết kế trusted proxy được review và test riêng.
- Foundation nghiệm thu một Rental process. Chưa có bằng chứng local deployment hỗ trợ SignalR nhiều replica; không xem Valkey hiện diện là đã có backplane.
- Nếu môi trường bắt buộc nhiều replica, phải bổ sung backplane và kiểm thử cross-instance trước rollout. Sticky sessions đơn thuần không giải quyết fan-out.

### Quy tắc client cần bàn giao

- Đăng ký handlers trước khi start; connect thành công rồi GET lại page hiện tại. Cần test khoảng trống lúc kết nối/join để không bỏ mutation.
- Debounce invalidation theo view và entity; gợi ý 150–300 ms, có giới hạn chờ tối đa để burst không trì hoãn GET vô hạn.
- Renter detail so sánh version theo `orderId`; supplier detail theo `subOrderId` dù version đến từ parent.
- List phải refresh cả khi event nói tới entity chưa có trong page: entity có thể vừa lọt vào hoặc rời status filter, làm đổi totalCount/pagination.
- Chỉ bỏ event cũ khi cache của đúng resource/view đã có dữ liệu bằng hoặc mới hơn. Không dùng max version của một row cho cả list.
- Event đến trong khi GET chạy: lưu trạng thái dirty/highest required version và GET thêm nếu response chưa bao phủ; response cũ không được ghi đè cache mới.
- Initial start fail có retry hữu hạn/backoff do client quản lý; automatic reconnect không thay thế xử lý initial start failure.
- Reconnect, browser resume/focus sau gián đoạn và network recovered luôn GET lại; disconnect đánh dấu dữ liệu có thể stale.
- Không bỏ polling theo dõi invoice Payment nếu frontend vẫn cần nó và Payment integration chưa thay thế; Rental Hub chỉ xác nhận dữ liệu Rental đã đổi.
- Scope repo này là backend + contract/hướng dẫn integration. Tích hợp UI thực tế thuộc task frontend riêng; không tuyên bố polling frontend đã được gỡ chỉ vì backend xong.

## 4. Phases — mỗi phase là một task và một commit sau khi đạt DoD

Thứ tự: P1 → P2 → P3 → P4 → P5 → P6 → P7 → P8.
P3 thuộc Gateway, phải thực hiện trong repository đó và commit riêng; không che việc thiếu route bằng cách chỉ nghiệm thu direct-service.
Mỗi phase cập nhật checklist tiến độ/test evidence trong tài liệu này. Chỉ commit khi kiểm tra của phase pass; không commit thay đổi ngoài scope và không tự push/deploy.

### P1 — Hoàn thiện REST version contract

**Mục tiêu:** cả list và detail đều đủ metadata để client chống event cũ, không breaking rename.

- Sửa `OrderQueryDtos`, `OrderQueryRecords`, hai query services và projections trong `RentalOrderRepository`.
- Thêm renter summary `Version`, supplier summary `OrderVersion`; detail giữ nguyên contract hiện có.
- Cập nhật constructor callers, fixtures/fakes thật sự cần version; không thêm default giả bằng 0 để giữ call site cũ.
- Giữ pagination/filter/AsNoTracking/projection server-side; không thêm query riêng cho từng row để lấy version.
- Kiểm thử JSON field names, parent version, cross-supplier ownership, empty page, invalid status và detail regression; thêm PostgreSQL query coverage phù hợp.

**DoD:** list/detail phản ánh cùng parent version cho cùng snapshot; không tạo persisted version mới, không thêm migration.

**Commit:** `feat(rental): expose aggregate versions in order list responses`

### P2 — Hub endpoint, identity và security foundation

**Mục tiêu:** Hub connect an toàn qua service test host, chưa cần nối toàn lifecycle.

- Tạo module transport nhỏ trong `Api` cho Hub, endpoint/DI và group naming; DTO/policy hỗ trợ nằm Application theo trách nhiệm.
- Cấu hình SignalR riêng; MVC JSON options không tự áp dụng cho Hub. Test payload camelCase và changeType đúng contract.
- Thêm human-only policy qua identity logic hiện hữu; mở query bearer token đúng path, kiểm tra header precedence.
- WebSockets-only và close khi authentication hết hạn như Payment; options cần thiết được validate lúc startup.
- Auto-join hai group của đúng user, abort khi join lỗi; không có arbitrary join method hoặc application send method công khai.
- Origin allowlist cho browser WebSocket; cấu hình phải phản ánh origin thật. Không dùng wildcard credentials hoặc thêm hàng loạt knobs từ Payment.
- Test JWT thật qua JWKS fixture: valid human, anonymous, service hợp lệ, invalid/expired token, malformed sub, query token không có tác dụng trên REST/path giả, group identity normalization.

**DoD:** negotiate authorization và WebSocket connection đều có test; test fixture mô phỏng trusted Gateway cấp internal key, không yêu cầu browser giữ key.

**Commit:** `feat(rental): add authenticated order invalidation hub`

### P3 — Gateway transport cho Rental Hub

**Mục tiêu:** public URL hoạt động qua Gateway thật, thống nhất cách expose Hub với Payment.

- Re-check Gateway revision/config đang dùng trước khi sửa vì config local đã có dấu hiệu lệch prefix Rental hiện tại.
- Thêm route negotiate POST và WebSocket GET exact cho `/api/v1/rental/hubs/orders`, ưu tiên trước catch-all, giữ nguyên downstream path.
- Kiểm tra Ocelot WebSocket path thực sự chạy injection `X-Api-Key`; không suy ra từ HTTP DelegatingHandler.
- Nếu WebSocket bypass handler, sửa đúng proxy/header forwarding owner trong Gateway và có integration test. Không bỏ middleware Rental để lách lỗi.
- Bảo đảm JWT query tới Rental nhưng không được áp dụng cho REST Gateway; nếu Hub route cho qua Gateway auth thì Rental vẫn bắt buộc validate JWT.
- Chứng minh proxy upgrade, Origin, token hết hạn, query/header forwarding và redaction access token qua integration tests.
- Không sửa chung routing REST ngoài lỗi bắt buộc phục vụ Hub; nếu phải sửa, ghi rõ compatibility impact và regression coverage.

**DoD:** human connect và nhận test notification qua Gateway, anonymous 401, service 403 khi JWT hợp lệ; không có secret phía browser.

**Commit tại Gateway:** `feat(gateway): proxy authenticated rental order hub connections`

### P4 — Notifier và luồng checkout/payment/delivery note

**Mục tiêu:** có luồng mutation → commit → notification đầu tiên, áp dụng cách Payment đang làm.

- Thêm notifier abstraction nhỏ ở Application, SignalR implementation tại Api; chỉ hai typed event methods, không generic group administration.
- Cài timeout/cancellation, Result và log lỗi có kiểm soát; không optional null notifier cho production DI, không fire-and-forget.
- Nối order creation sau khi Order thực sự persist; attach invoice tại `CheckoutInvoiceIssuer`; payment confirmed tại transaction owner của payment dispatcher.
- Nối delivery note: invalidation renter và các supplier đã được phép xem phần dữ liệu này.
- Chốt và test cách staged application handlers trả metadata tới outer transaction owner; không phát trong inner SaveChanges.
- Không lấy notification PaymentCompleted từ Payment làm bằng chứng Rental đã Paid; chỉ phát khi Rental apply payment event và commit.
- Đơn chưa trả tiền chỉ báo renter. Lúc chuyển đủ điều kiện supplier nhìn thấy: báo đúng từng SubOrder để supplier list xuất hiện dữ liệu mới.
- Test duplicate payment/invoice, rejected proof, rollback, DB concurrency, push timeout và request cancel sau commit; push failure không đổi business outcome.

**DoD:** nhận event rồi GET thấy dữ liệu đã commit và version đủ mới; rollback/no-op không phát notification thành công giả.

**Commit:** `feat(rental): notify committed checkout and payment changes`

### P5 — Supplier confirmation và handover

**Mục tiêu:** cập nhật realtime đúng completion boundary của workflow Inventory.

- Nối accept/reject SubOrder và Inventory allocation confirmation vào notifier, giữ một đường xử lý nghiệp vụ hiện hữu.
- `SUPPLIER_CONFIRMED` chỉ khi reply đã làm dữ liệu chuyển trạng thái; reject báo đúng phần supplier và renter.
- Handover phát sau Inventory success, evidence persistence và outer commit; command accepted hoặc Inventory rejected không phát HANDOVER_CONFIRMED.
- Test mixed-supplier Order: supplier A không nhận thay đổi riêng B, renter nhận invalidation không có physical data.
- Test confirm nhiều item: intermediate/final commit theo dữ liệu thực tế, version đúng; retry receipt/duplicate inbox không nhân side effect.
- Chỉ notifier/payload mới; không di chuyển state machine hoặc refactor các entity cũ ngoài phạm vi realtime.

**DoD:** HTTP/Kafka tests chứng minh đúng recipient, completion timing, rollback/replay và payload whitelist.

**Commit:** `feat(rental): notify supplier decisions and confirmed handovers`

### P6 — Cancellation, expiry và handover overdue

**Mục tiêu:** thay đổi từ renter, scheduler và operation executor đều invalidation đúng.

- Nối order/item cancellation, supplier confirmation timeout, payment expiry và handover overdue/auto-expiry.
- Kiểm tra cả retry worker/executor paths, không chỉ HTTP entrypoint.
- Phân biệt overdue warning với expired và release/refund đang chờ; publish theo dữ liệu đọc thực tế, không tự gọi là completed.
- Payment expiry trước paid không báo supplier; mutation phạm vi một SubOrder không broadcast các supplier khác.
- Test concurrent scheduler/user action, stale state, no-op rerun, Inventory uncertainty và rollback; giữ nguyên các deadline/invariant hiện tại.

**DoD:** mỗi observable commit có đúng notification scope, không thay đổi business timing để phục vụ realtime.

**Commit:** `feat(rental): notify cancellation expiry and overdue changes`

### P7 — Return, refund và settlement

**Mục tiêu:** đóng coverage cho cập nhật sau bàn giao, kể cả parent status không đổi.

- Nối return khi Inventory result được apply và commit; rejected return không phát RETURN_RECORDED.
- Bao phủ partial return, damage/deduction proposal, renter accept/dispute, admin resolution và các timeout settlement hiện hữu.
- Nối RefundCompleted/RentalSettled tại outer application transaction owner; chỉ báo supplier nếu projection supplier thay đổi hoặc visibility của SubOrder thay đổi.
- Audit toàn bộ caller `RecordReadModelUpdate`, `AttachInvoice`, creation và các mutation không đi qua hai symbol này so với bốn GET. Mỗi đường phải có test hoặc lý do không cần event.
- Không biến receipt-only retry/technical bookkeeping thành user-facing change nếu bốn GET không thay đổi.
- Test replay cùng/new message ID, atomic rollback, partial return giữ nguyên parent status nhưng tăng version; event cùng parent version cho nhiều SubOrder không bị dedup sai scope.

**DoD:** có mutation coverage matrix với nguồn thay đổi, transaction owner, recipient, changeType và test chứng minh; không còn hai nơi cùng phát cho một commit do lồng service/dispatcher.

**Commit:** `feat(rental): notify return refund and settlement updates`

### P8 — Nghiệm thu end-to-end và bàn giao client

**Mục tiêu:** chứng minh foundation dùng được qua browser/Gateway và ghi rõ giới hạn.

- Integration test real WebSocket/SignalR protocol, JWT thật và PostgreSQL: 2 renter + 2 supplier, Order nhiều SubOrder, nhiều tab của cùng user.
- Test gọi arbitrary join/subscribe không tồn tại, client-supplied orderId/userId/supplierId không đổi group; assert cả người được nhận và người không được nhận với timeout hữu hạn.
- Chạy flow payment → supplier confirm → handover → return/settlement; nhận event xong gọi lại bốn GET để kiểm tra version và data isolation.
- Test disconnect/reconnect, token refresh/expiry, socket cleanup, mất notification rồi REST resync; handshake readiness không dựa vào sleep may rủi.
- Smoke test qua Gateway: route path, negotiate và upgrade, internal key, origin, query log redaction. Nếu không có môi trường Gateway, báo rõ chưa nghiệm thu phần này.
- Ghi hướng dẫn frontend debounce, filter/pagination, version theo resource, concurrent GET và reconnect/resume; không viết UI mới trong Rental.
- Ghi topology một instance và điều kiện cần backplane; log push fail/timeout đủ đo vận hành nhưng không thêm monitoring stack riêng.
- Review full diff, references, DI/options/dependency changes và docs; không thêm SignalR package server nếu shared framework hiện tại đã đủ. Client test package chỉ thêm khi cần.

**DoD:** mọi AC bên dưới có test evidence hoặc ghi rõ giới hạn chưa đáp ứng; không tuyên bố UI đã ngừng polling nếu chưa có task/frontend evidence.

**Commit:** `test(rental): validate realtime isolation and document client resync`

## 5. Acceptance Criteria sau điều chỉnh

1. Human JWT hợp lệ connect Hub qua public URL; anonymous 401; authenticated service JWT bị 403.
2. JWT invalid/expired sai issuer/audience bị 401; chỉ Hub được dùng query token, bearer header có precedence.
3. User A không thể join/nhận group B; nhiều tab cùng user nhận được; không có admin global broadcast.
4. Supplier chỉ nhận notification đúng SubOrder được phép đọc, không biết order PendingPayment qua realtime.
5. Event không có PII/physical/internal payload; renter REST tiếp tục không có AssetId/AllocationId.
6. Renter list/detail có `version`; supplier list/detail có `orderVersion`, tất cả lấy parent aggregate version.
7. Chỉ phát notification về thay đổi đã commit. Receipt accepted không bị gọi là business completion; rollback/no-op/replay không tạo thông báo hoàn tất giả.
8. Thay đổi dữ liệu hiển thị được invalidation dù Order.Status không đổi; giữ nguyên REST contract ngoài additive list version fields.
9. Push timeout/failure không rollback mutation đã commit, không trả về lỗi làm client retry mutation đã thành công; lỗi được ghi nhận.
10. Initial connect/reconnect/resume/network recovered có hướng dẫn và kiểm thử REST resync; không yêu cầu event replay/ACK/durable SignalR.
11. Public Hub qua Gateway hoạt động mà frontend không biết internal key; route rewrite, query token và Origin có coverage.
12. Giới hạn một process được công bố. Nếu chạy nhiều replica, backplane/cross-instance delivery phải được bổ sung trước rollout.

## 6. Kiểm tra và quy tắc commit

Áp dụng mỗi phase có đổi code trong repository liên quan:

```bash
dotnet build RentalService.sln --configuration Release --nologo
dotnet test RentalService/tests/Api.Tests/RentalService.Api.Tests.csproj --configuration Release --no-build --nologo
dotnet format RentalService.sln --verify-no-changes --no-restore
git diff --check
```

- Build bật analyzers và TreatWarningsAsErrors theo `Directory.Build.props`. Chạy targeted tests khi phát triển, full affected suite trước commit.
- P3 dùng solution/test projects thực tế của Gateway, không dùng lệnh Rental để tuyên bố Gateway pass.
- Test PostgreSQL cần `POSTGRESQL_TEST_CONNECTION_STRING` hoặc `RENTAL_TEST_POSTGRES_CONNECTION`, hoặc `.env`/PostgreSQL local theo helper hiện có. Không in connection string/password.
- Phải kiểm tra số tests bị skip/early-return do thiếu PostgreSQL; test process exit 0 không tự chứng minh integration đã chạy.
- Nếu đổi EF model ngoài dự kiến, review lý do và migration; kế hoạch foundation này không yêu cầu schema change.
- Trước commit: đọc full diff, tìm references obsolete, kiểm tra không có secrets/generated artifacts/debug output; stage đúng files của phase.
- Báo commit hash, phạm vi thực hiện, checks đã chạy, checks chưa chạy và lý do. Phase không pass thì sửa hoặc báo blocker; không ghi done.
- Không đổi Kafka contracts, event producer/outbox và PaymentService chỉ để phát SignalR; giữ một authoritative Rental workflow và một typed notification path.
- Vì đây là capability mới, không có realtime implementation cũ cần xóa. Chỉ xóa/sửa scaffolding, overload hoặc test assumption thực sự obsolete do thay đổi trong phase; không xóa code không liên quan để tạo diff cleanup.

## 7. Ngoài phạm vi

Durable SignalR outbox/replay, event sourcing, frontend implementation, dynamic resource subscription, admin watch-all, full Order push, schema/version migration, generic notification framework, refactor Domain toàn service và sửa PaymentService.
Multi-replica backplane là điều kiện triển khai thêm nếu topology yêu cầu, không mặc định thêm chỉ vì deployment có Valkey.

## 8. Nguồn tham khảo bên ngoài

- [SignalR authentication/authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0): browser token transport và token expiration.
- [SignalR security](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0): Origin/CORS và query-token logging.
- [SignalR hosting/scaling](https://learn.microsoft.com/en-us/aspnet/core/signalr/scale?view=aspnetcore-10.0): fan-out giữa process và session affinity.
- [SignalR JavaScript client](https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client?view=aspnetcore-10.0): reconnect và initial-start failure.

## 9. Evidence của bước lập kế hoạch

Đã đọc Project Guide, khảo sát graph/symbol/callers và code/test hiện hữu của Rental, đọc Payment Git snapshot và Gateway config/forwarding code local.
Chưa chạy ứng dụng, build/test hoặc kết nối Gateway: bước này chỉ tạo tài liệu, theo quy tắc documentation-only không chạy build/test ứng dụng.
Không coi bất kỳ proposed phase hay acceptance criterion nào là đã được thực thi.
