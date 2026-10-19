# DPRental — Project & Agent Guide

> **Mục đích của tài liệu này**
>
> Đây là tài liệu ngữ cảnh chung cho toàn bộ dự án DPRental, đặc biệt dành cho developer/coding agent khi nhận một Jira Task/Subtask độc lập.
>
> Agent **không được chỉ đọc tên task rồi tự suy diễn domain**. Trước khi thực hiện task, phải đọc tài liệu này, kiểm tra source code hiện tại của service liên quan, sau đó đối chiếu acceptance criteria của task.
>
> Tài liệu này mô tả **target architecture cho MVP**. Một số source code/SRS cũ có thể chưa được refactor theo target này.

---

## 1. Thứ tự ưu tiên khi có mâu thuẫn

Khi requirement, source code và tài liệu khác nhau, áp dụng thứ tự:

1. **Jira Task/Subtask hiện tại và Acceptance Criteria đã được duyệt**
2. **Các quyết định kiến trúc/MVP trong tài liệu này**
3. **SRS mới nhất của service**
4. **Contract đã được freeze giữa các service**
5. **Source code hiện tại**
6. README/tài liệu cũ

Nếu phát hiện mâu thuẫn ảnh hưởng domain model hoặc public/internal contract:

- Không tự ý chọn một phương án khác.
- Không âm thầm thay đổi contract của service khác.
- Ghi rõ conflict trong phần phân tích task.
- Chỉ triển khai theo phương án đã được task hoặc architecture guide xác nhận.

---

# 2. Product Overview

DPRental là nền tảng trung gian kết nối:

```text
Supplier / Chủ thiết bị
        │
        │ đăng thiết bị cho thuê
        ▼
    DPRental Platform
        │
        │ discovery / booking / payment / rental lifecycle
        ▼
       Renter
```

DPRental **không phải là chủ sở hữu mặc định của tài sản**.

MVP tập trung vào **serialized rental assets**: mỗi tài sản vật lý có identity riêng và có thể theo dõi riêng bằng AssetId/AssetCode/SerialNumber.

Ví dụ:

```text
EquipmentListing
Sony A7 IV
500.000 VND/ngày

├── CAM-001 / Serial SN001
├── CAM-002 / Serial SN002
└── CAM-003 / Serial SN003
```

Customer nhìn thấy và thuê `EquipmentListing`.

Inventory quản lý các physical `InventoryAsset` phía sau.

---

# 3. MVP Principles

## 3.1. Không over-engineer

Không xây trước các abstraction chưa có use case MVP.

Không tự ý thêm generic workflow engine, dynamic pricing engine, stock pool cho non-serialized inventory, organization ownership migration, bundle/kit engine, warehouse movement ledger, generic rule engine, event sourcing hoặc distributed saga framework phức tạp.

Nếu task không yêu cầu thì không triển khai.

## 3.2. Serialized Asset là model chính của MVP

Mỗi physical asset được tạo **từng cái một**.

Không dùng:

```json
{
  "quantity": 5
}
```

để sinh 5 physical assets.

Mỗi asset có identity riêng:

```text
InventoryAsset
- Id
- EquipmentListingId
- AssetCode
- SerialNumber?
- ManufactureYear?
- OperationalStatus
- AcceptingBookings
```

`AssetCode` phải là identity nội bộ ổn định.

`SerialNumber` có thể nullable nếu loại thiết bị không có serial.

## 3.3. Quantity là dữ liệu suy ra ở Inventory

Không persist `EquipmentListing.Quantity` như source of truth.

```text
TotalQuantity
= COUNT(active physical assets thuộc listing)
```

Availability cho khoảng thuê:

```text
AvailableQuantity
= số physical assets rảnh xuyên suốt toàn bộ requested interval
```

Không được tính range availability bằng:

```text
MIN(dailyAvailableQuantity)
```

vì các ngày có thể rảnh trên những asset khác nhau.

---

# 4. Core Domain Model

```text
Catalog
  EquipmentType
  SpecificationSchema
        │
        ▼
Inventory
  EquipmentListing
        │ 1
        │
        │ N
        ▼
  InventoryAsset
        │
        ├── AssetAllocation
        └── CalendarBlock
        │
        ▼
Rental
  RentalOrder
        │
        └── RentalItem
               │
               └── RentalUnitAssignment
                      ↕
                Inventory Allocation
```

---

# 5. EquipmentListing vs InventoryAsset

Đây là distinction quan trọng nhất của project.

## 5.1. EquipmentListing

`EquipmentListing` là đơn vị thương mại/public mà renter nhìn thấy và thuê.

Nó chứa thông tin chung:

```text
EquipmentListing
- Id
- OwnerUserId                 // MVP hiện tại
- EquipmentTypeId
- BrandId
- Model
- SchemaVersion
- SpecValues
- Title / Description
- Thumbnail
- DailyRentalRate
- Deposit
- Pickup/Service Location
- Listing/Booking Status
```

Listing chịu trách nhiệm cho:

- thông tin public;
- thông số chung;
- giá thuê;
- tiền cọc;
- vị trí/pickup chung;
- marketplace discovery;
- grouping các physical assets tương đương.

## 5.2. InventoryAsset

`InventoryAsset` đại diện cho **một thiết bị vật lý thật**.

```text
InventoryAsset
- Id
- EquipmentListingId
- AssetCode
- SerialNumber
- ManufactureYear
- OperationalStatus
- AcceptingBookings
```

Asset không nên giữ duplicated commercial price của Listing.

Asset chịu trách nhiệm cho:

- physical identity;
- serial;
- condition/operational state;
- maintenance/block;
- allocations;
- asset-level calendar;
- fulfillment.

## 5.3. Tạo thiết bị

UI có thể gọi hành động:

```text
Thêm thiết bị
```

Lần đầu:

```text
transaction
    Create EquipmentListing
    +
    Create first InventoryAsset
```

Khi listing đã tồn tại:

```text
POST /equipment-listings/{listingId}/assets
```

chỉ tạo thêm **một physical asset**.

Không nhập quantity.

---

# 6. Shared Calendar

Không tạo bảng `ListingCalendar` làm source of truth.

Source of truth:

```text
AssetAllocation
CalendarBlock
```

đều gắn với physical `InventoryAsset`.

Listing calendar chỉ là projection:

```text
EquipmentListing
      ↓
all child assets
      ↓
allocations + blocks
      ↓
aggregate
```

Ví dụ:

```text
Sony A7 IV — 10/10

Total       5
Occupied    2
Blocked     1
Available   2
```

Drill-down có thể hiển thị asset nào đang occupied/blocked.

---

# 7. Inventory Allocation Rules

Rental/client không chọn physical asset.

Rental gửi:

```text
ListingId
Quantity
StartDate
EndDate
ExternalReference
```

Inventory:

1. lock group/listing để chống race condition;
2. re-query availability dưới lock;
3. tìm N physical assets rảnh xuyên suốt interval;
4. tạo N `AssetAllocation`;
5. commit atomically;
6. trả AllocationGroup/assigned assets.

Ví dụ:

```text
Listing has 5 available assets

Request A = 3
Request B = 3

Hai request concurrent không được cùng thành công.
```

## 7.1. Multi-unit idempotency

Không dùng cùng một unique `ExternalReference` cho nhiều physical allocation nếu DB hiện unique `(SourceSystem, ExternalReference)`.

Logical request cần group/business reference riêng.

```text
GroupReference = rental-item:RI001

Allocations:
RI001:CAM-001
RI001:CAM-003
RI001:CAM-005
```

Retry cùng logical request phải trả cùng allocation set hoặc trạng thái tương đương, không tạo duplicate.

---

# 8. Availability Rules

Một listing khả dụng cho request khi có **ít nhất N physical assets rảnh xuyên suốt toàn interval**.

Ví dụ:

```text
A1: day 1 free, day 2 busy
A2: day 1 busy, day 2 free
```

Daily calendar:

```text
day 1 available = 1
day 2 available = 1
```

Nhưng availability cho:

```text
day 1 → day 2
quantity = 1
```

là `0` vì không có cùng một asset rảnh xuyên suốt cả khoảng.

---

# 9. Rental MVP — Canonical Model

## 9.1. Cart

```text
RentalCart
└── RentalCartItem
    - EquipmentListingId
    - Quantity
    - ExpectedStartDate
    - ExpectedEndDate
```

Cart:

- không giữ lịch;
- không giữ giá;
- không lưu AssetId;
- mỗi lần mở/check-out phải revalidate Listing, price và availability.

## 9.2. RentalOrder

```text
RentalOrder
- Id
- OrderCode
- RenterId
- Status
- DeliverySnapshot
- RefundDestinationSnapshot/reference
- TermsVersion
- TermsAcceptedAt
- PaymentDeadline
- CreatedAt
```

Một Order có thể có nhiều `RentalItem`, kể cả từ nhiều supplier.

Payment được thực hiện một lần cho toàn Order.

## 9.3. RentalItem

Một `RentalItem` đại diện cho:

```text
1 EquipmentListing
+
1 rental interval
+
requested Quantity
```

Không đại diện trực tiếp cho một physical asset.

```text
RentalItem
- Id
- RentalOrderId
- EquipmentListingId
- SupplierId
- Quantity
- ExpectedStartDate
- ExpectedEndDate
- ListingSnapshot
- DailyRateSnapshot
- DepositPerUnitSnapshot
- RentalAmount
- DepositAmount
- Status
- AllocationGroupId
```

## 9.4. RentalUnitAssignment

Inventory có thể early-bind physical assets ở lúc hold.

Rental lưu internal mapping tối thiểu:

```text
RentalUnitAssignment
- RentalItemId
- AssetId
- AllocationId
```

Customer không cần chọn hoặc nhìn thấy AssetId/SerialNumber trong flow bình thường.

Mapping này phục vụ handover, return, damage, maintenance và audit.

---

# 10. Rental State Machine

```text
PENDING_PAYMENT
        │ payment success
        ▼
PENDING_SUPPLIER_CONFIRMATION
        │
        ├── reject     → REJECTED
        ├── timeout    → EXPIRED
        │
        ▼
AWAITING_HANDOVER
        │
        ├── cancel/not-delivered → CANCELLED
        │
        ▼
IN_RENTAL
        │
        ├── normal return
        │       ↓
        │   COMPLETED
        │
        └── damaged return
                ↓
        PENDING_SETTLEMENT
                ↓
           COMPLETED
```

Không được set `Status` trực tiếp tùy ý.

Mọi state transition phải do Application Service thực thi, kiểm tra current state, actor, preconditions, idempotency và side effects; không để Domain Entity hay Repository tự chuyển trạng thái.

---

# 11. Rental Checkout Flow

```text
Cart
 ↓
Revalidate listing / price / dates / quantity
 ↓
Create logical RentalItems
 ↓
Inventory holds all requested units
 ↓
Persist immutable snapshots
 ↓
Create invoice
 ↓
PENDING_PAYMENT
```

## 11.1. All-or-nothing checkout

MVP dùng:

```text
all selected cart lines hold successfully
→ create payable order
```

Nếu bất kỳ line nào không giữ được:

```text
release successful holds
do not create payable order
return conflict details
```

Không tự checkout partial cart trong MVP.

---

# 12. Payment and Supplier Confirmation

```text
Submit Order
    ↓
Inventory hold
    ↓
Payment deadline = 30 minutes
    ↓
InvoicePaid
    ↓
PENDING_SUPPLIER_CONFIRMATION
    ↓
Supplier Accept / Reject
```

Supplier chỉ nhìn thấy RentalItem sau khi Order được thanh toán.

Supplier reject hoặc không phản hồi đúng hạn:

```text
release/cancel Inventory allocation
request 100% refund for RentalItem
```

Các item khác trong cùng Order không bị ảnh hưởng.

Rental không trực tiếp chuyển tiền.

Payment/Billing service chịu trách nhiệm invoice, payment status, refund, platform fee và supplier payout.

---

# 13. Handover

Supplier ghi nhận handover với:

```text
ActualHandoverAt
MediaIds[]
Note
Confirmation
```

Evidence sau khi submit là immutable.

RentalItem:

```text
AWAITING_HANDOVER
→ IN_RENTAL
```

MVP có thể hỗ trợ renter acknowledgement như evidence bổ sung nhưng không cần thêm một state riêng.

---

# 14. Return, Damage and Overdue

## 14.1. Normal return

Nếu asset được trả bình thường:

```text
record return
→ release rental allocation
→ asset có thể available trở lại
→ settle deposit = 0 deduction
→ RentalItem COMPLETED
```

Trả sớm không refund rental fee trong MVP.

## 14.2. Damaged return

Không được:

```text
return damaged
→ release
→ asset immediately available
```

Phải:

```text
return damaged
→ close/release rental allocation
→ block damaged physical asset
→ MAINTENANCE / OUT_OF_SERVICE / CalendarBlock equivalent
→ PENDING_SETTLEMENT
```

Asset chỉ được available lại khi Inventory/maintenance flow cho phép.

Nếu RentalItem quantity > 1, return/condition phải xác định được theo từng `RentalUnitAssignment`.

## 14.3. Overdue

Nếu:

```text
now > ExpectedEndDate
AND asset chưa actual return
```

asset vẫn phải unavailable.

MVP không cần late fee nhưng phải bảo vệ calendar.

Không được để physical asset trở lại available trong khi renter chưa trả.

---

# 15. Deposit Settlement MVP

Nếu damage:

```text
Supplier submits deduction proposal
        ↓
Renter ACCEPT / DISPUTE
        ↓
Admin resolves dispute if needed
        ↓
Payment settlement
        ↓
COMPLETED
```

MVP rule:

- supplier có 48h gửi deduction;
- quá hạn → deduction = 0;
- renter có 48h phản hồi;
- quá hạn → coi như đồng ý;
- deduction không vượt deposit;
- Admin decision là final trong hệ thống.

Rental tính và lưu quyết định nghiệp vụ.

Payment thực hiện dòng tiền.

---

# 16. Service Boundaries

## AuthService

Sở hữu authentication, JWT, user identity, profile và roles/permissions.

Service khác không copy user table làm source of truth.

## CatalogService

Sở hữu EquipmentType, Brand, Specification Schema, schema versions và dynamic specification validation.

Catalog không quản lý physical inventory, rental availability, allocations hoặc RentalOrder.

## MediaService

Sở hữu media lifecycle, upload, public/private media và content delivery.

Domain service chỉ lưu `MediaId`/reference.

## InventoryService

Sở hữu:

- EquipmentListing;
- physical InventoryAsset;
- listing rental rate/deposit theo target MVP;
- availability;
- AssetAllocation;
- CalendarBlock;
- shared listing calendar projection;
- physical asset assignment.

Inventory không sở hữu RentalOrder lifecycle, payment, refund hoặc supplier payout.

## RentalService

Sở hữu:

- cart;
- RentalOrder;
- RentalItem;
- commercial snapshots lúc checkout;
- supplier decision;
- rental lifecycle;
- handover/return evidence;
- cancellation;
- deduction/dispute decision;
- Rental state history.

Rental không tự quyết định asset availability và không tự chuyển tiền.

## Payment / Invoice Service

Sở hữu invoice, collection/payment status, refund, platform fee, settlement và supplier payout.

## Gateway

Sở hữu routing và gateway-level concerns. Business authorization vẫn phải được service đích thực thi.

## Notification / Reporting

Nên consume domain/integration events, không nhét logic gửi notification/report vào entities hay application service cốt lõi.

---

# 17. Ownership MVP

Current MVP giữ ownership theo user nếu service hiện tại đang dùng:

```text
OwnerUserId = JWT.sub
```

Không tự ý migrate sang `SupplierOrganizationId`, `OrganizationId` hoặc `TenantId` trừ khi có Jira Task riêng.

---

# 18. API and Integration Rules

## 18.1. Internal service calls

Internal contract phải:

- có request/response DTO rõ ràng;
- timeout hữu hạn;
- propagate `CancellationToken`;
- không retry business validation error;
- map dependency failure theo contract đã thống nhất;
- không leak exception/raw upstream body cho client.

## 18.2. No cross-service database coupling

Không query trực tiếp database của service khác, không tạo foreign key cross-service DB, không join table across service databases.

Chỉ lưu stable external IDs/reference.

## 18.3. Idempotency

Các operation có side effect phải retry-safe, đặc biệt:

```text
Checkout
CreateHold
ConfirmAllocation
ReleaseAllocation
SupplierAccept
SupplierReject
MarkHandover
MarkReturn
CreateInvoice
RequestRefund
Settlement
```

Retry cùng logical operation không được tạo duplicate.

## 18.4. Concurrency

Các operation availability/allocation phải chống race condition ở nơi sở hữu dữ liệu: **Inventory**.

Rental không được dùng pattern `check available` rồi rất lâu sau mới create hold mà không có atomic protection phía Inventory.

---

# 19. Events, Inbox and Outbox

Service có side effect asynchronous nên dùng Outbox.

Consumer cần Inbox/deduplication nếu event có thể được delivery lại.

Ví dụ:

```text
InvoicePaid
RefundCompleted

RentalOrderCreated
RentalItemConfirmed
RentalItemRejected
RentalStarted
RentalReturned
RentalCompleted

InventoryAssetAvailabilityChanged
```

Event nên có:

```text
eventId
eventType
occurredAt
aggregateId
correlationId
schemaVersion
```

Không assume exactly-once delivery.

---

# 20. Coding Architecture

Kiến trúc mã nguồn của **RentalService** tuân thủ cấu trúc phân tầng và tổ chức thư mục sau:

```text
RentalService/
  src/
    Common/                     // Enums, Result/Error scaffold, primitives dùng chung
    Domain/                     // Dữ liệu thuần túy, entities, DbContext, repositories
      Entities/                 // Data models, relationships, navigation properties, EF mapping
      IRepositories/            // Contracts cho repositories và query records
      Repositories/             // DbContext, EF Core repository implementations
      Migrations/               // EF Core database migrations
    Application/                // Trung tâm toàn bộ nghiệp vụ của service
      Services/<Feature>/       // Use case services thực thi toàn bộ logic nghiệp vụ
      IServices/<Feature>/      // Consumer-focused interfaces cho services (tuân thủ ISP)
      DTOs/<Feature>/           // Request/Response contracts, internal DTOs
      Policies/                 // Business policies, transition rules, timing rules
      Validation/               // Business validators
      Security/                 // Authorization policies, ownership checks
      Clients/                  // Abstractions và implementations gọi service ngoài (HTTP)
      Kafka/                    // Event consumers, producers, workers, event specs
      Background/               // Periodic workers (deadline, recovery)
      DependencyInjection/      // DI wiring cho application layer
    Api/                        // Web API entrypoint, transport concerns
      Controllers/              // Mỏng, tiếp nhận HTTP request và dispatch sang Application
  tests/
    Api.Tests/                  // Unit tests (Application) và Integration tests (Database/Api)
```

> **Lưu ý kiến trúc đặc thù của RentalService**:
> DbContext và các EF Core repository implementations được đặt trực tiếp trong `RentalService.Domain/Repositories` (tương đồng với AuthService), **không tạo thêm project Infrastructure riêng**. Các ValueObjects được inline trực tiếp thành các thuộc tính/fields trên entities sở hữu kèm DataAnnotations hoặc EF Core Fluent mapping.

## 20.1. Domain và Entities (Dữ liệu thuần túy & Persistence Mapping)

Tầng Domain và các Entities có trách nhiệm:
- Lưu trữ trạng thái dữ liệu, định nghĩa cấu trúc bảng và quan hệ giữa các bảng (1-N, N-N, foreign keys, navigation properties).
- Cung cấp EF Core mapping qua DataAnnotations hoặc Fluent API trong `RentalServiceDbContext`.
- Thực hiện các thao tác dữ liệu đơn giản nội bộ (như gán giá trị thuộc tính qua setter/internal method, thêm bản ghi con vào internal collection/list).

**Quy tắc nghiêm ngặt**:
- **KHÔNG triển khai nghiệp vụ tại Domain**: Tuyệt đối không đặt workflow, công thức tính toán nghiệp vụ, quyết định quyền/ownership, tính hạn/deadline, hoặc logic chuyển trạng thái (state transitions) bên trong Entities hay Domain.
- **Nghiêm cấm lách luật**: Không chuyển logic nghiệp vụ sang entity constructor, computed property, extension method hay tạo "Domain Policy" để né quy tắc này. Toàn bộ logic nghiệp vụ phải nằm tại Application Service.

## 20.2. Application (Trung tâm sở hữu mọi quyết định nghiệp vụ)

Application Service là nơi duy nhất **sở hữu và điều phối toàn bộ quyết định nghiệp vụ** của RentalService.

Mỗi Application Service chịu trách nhiệm:
- **Kiểm tra quyền và sở hữu**: Xác thực quyền hạn của actor (Renter, Supplier, Admin, System) và quyền sở hữu đơn hàng (chống IDOR).
- **Thực thi và bảo toàn Business Invariants**: Kiểm tra toàn bộ điều kiện tiên quyết (preconditions), trạng thái hợp lệ trước khi cho phép hành động.
- **Tính toán tiền và tài chính**: Tính tiền thuê (`RentalAmount`), tiền cọc (`DepositAmount`), đề xuất khấu trừ (`TotalProposedAmount`), tiền chốt bồi thường (`FinalDeductionAmount`).
- **Quản lý chuyển trạng thái (State Machine Transitions) & Workflows**: Quyết định khi nào chuyển trạng thái đơn hàng/mục thuê (ví dụ: `PendingPayment` → `PendingSupplierConfirmation` → `AwaitingHandover` → `InRental` → `Completed` / `PendingSettlement`), ghi nhận `RentalStateTransition` với đầy đủ audit trail (Action, ActorRole, ActorId, OccurredAt, OperationId).
- **Tính toán thời hạn (Deadlines)**: Tính hạn thanh toán (payment deadline), hạn nhà cung cấp xác nhận (supplier confirmation deadline), hạn giữ thiết bị (hold expiry), hạn renter phản hồi khấu trừ (renter response deadline).
- **Xử lý Idempotency**: Đảm bảo an toàn khi retry các tác vụ nhạy cảm (Checkout, Payment, Accept, Reject, Handover, Return, Deduction) bằng idempotency key / operation receipt.
- **Điều phối Transaction và Tương tác ngoại vi**: Điều phối persistence qua `IRepositories`, phát command/event qua Kafka, gọi external HTTP clients (Inventory, Payment).

**Chính sách và Validator nghiệp vụ**:
Mọi Policy hoặc Validator hỗ trợ nghiệp vụ (như `CheckoutRules`, `SubOrderStatusPolicy`, `RentalAuthorizationPolicy`, quy tắc thời gian đặt trước) phải nằm tại `Application/Policies` hoặc `Application/Validation` và được Application Service trực tiếp sử dụng.

## 20.3. Repository (Truy vấn, Persistence & Data Access)

Repository chỉ phục vụ việc truy vấn, lưu trữ dữ liệu, projection, pagination, transaction/locking và concurrency ở mức persistence.
- **Trách nhiệm của Repository**: Thực hiện các thao tác CRUD, đẩy các câu truy vấn lọc (filtering), sắp xếp (sorting), tổng hợp (aggregation) và phân trang (pagination) xuống database bằng LINQ/EF Core. Quản lý transaction, optimistic concurrency và database locks.
- **Trách nhiệm của Application**: Application Service quyết định các tiêu chí nghiệp vụ (truyền tham số/bộ lọc vào Repository), nhận dữ liệu về và kiểm tra lại toàn bộ điều kiện nghiệp vụ trước khi ghi dữ liệu.
- **Ranh giới rõ ràng**: Repository tuyệt đối không tự ý đưa ra quyết định nghiệp vụ hay tự ý chuyển trạng thái entity ngoài chỉ định của Application Service.

## 20.4. API và Background Entrypoints (Giao tiếp & Dispatch)

Các entrypoint giao tiếp (API Controllers, Background Workers, Kafka Consumer Workers):
- Chỉ chịu trách nhiệm tiếp nhận yêu cầu từ bên ngoài, giải mã (deserialize), trích xuất context giao tiếp (JWT claims, correlation ID), gọi Application Service tương ứng, và map kết quả (`Result<T>`) sang phản hồi phù hợp (`ProblemDetails`, HTTP status codes, Kafka ACK).
- **Không chứa quyết định nghiệp vụ**: Controller và Worker không tự quyết định trạng thái hay nghiệp vụ; mọi logic phải được ủy quyền cho Application Service.
- **Không truy cập DbContext trực tiếp**: Tuyệt đối không để Controller hay Application Service truy cập `DbContext` trực tiếp nhằm né Repository. Mọi thao tác persistence bắt buộc đi qua abstraction `IRepositories`.

## 20.5. Nguyên lý SOLID và Tính Nhất Quán

Thiết kế code trong RentalService phải tuân thủ nghiêm ngặt các nguyên lý SOLID:

- **SRP (Single Responsibility Principle - Đơn trách nhiệm)**:
  - Chia nhỏ Application Service theo từng use case / luồng nghiệp vụ cụ thể (ví dụ: `CheckoutService`, `ReturnService`, `DeductionService`, `SupplierDecisionService`, `RenterOrderQueryService`, `AdminOrderQueryService`).
  - Tránh tuyệt đối "God Service" hoặc "God Repository" ôm đồm quá nhiều trách nhiệm không liên quan.
- **OCP (Open/Closed Principle - Đóng mở)**:
  - Tận dụng tối đa các component và abstractions hiện có. Chỉ thêm điểm mở rộng khi có nhu cầu nghiệp vụ thực tế từ Jira Task; không suy đoán tương lai hay tạo abstraction phỏng đoán (MVP Principle: không over-engineer).
- **LSP (Liskov Substitution Principle - Thay thế Liskov)**:
  - Mọi implementation của interface phải tuân thủ đúng contract đã định nghĩa, bao gồm kiểu dữ liệu trả về, mã lỗi (`Result`/`ErrorType`) và hành vi biên.
  - Tuyệt đối không được "giả vờ thành công" (fake success) hoặc âm thầm trả về tập dữ liệu rỗng đối với các tính năng chưa được hỗ trợ.
- **ISP (Interface Segregation Principle - Phân tách giao diện)**:
  - Thiết kế interface nhỏ gọn, tập trung chính xác vào nhu cầu của client/consumer (ví dụ tách biệt: `IRenterOrderQueryService`, `ISupplierSubOrderQueryService`, `IAdminOrderQueryService`, `IRentalOrderRepository`, `IOrderLockManager`).
  - Không tạo interface máy móc kiểu 1-class-1-interface nếu không có nhiều consumer hoặc không phục vụ mục đích kiểm thử/decoupling thực tế.
- **DIP (Dependency Inversion Principle - Đảo ngược phụ thuộc)**:
  - Application Service phụ thuộc vào abstractions thông qua Dependency Injection (`IRentalOrderRepository`, `IOrderLockManager`, `IInventoryRentalClient`, `TimeProvider`).
  - Domain hoàn toàn độc lập, không tham chiếu ngược lại Application hay API.
  - Giữ nguyên vị trí repository contracts hiện có trong `RentalService.Domain/IRepositories`.
---

# 21. Result and Error Convention

Theo scaffold hiện tại:

```text
Result
Result<T>
Error
ErrorType
```

Expected business error trả `Result`, không throw exception để điều khiển flow.

Controller map sang `ProblemDetails`.

Không trả raw exception message/stack trace/payload ra ngoài.

---

# 22. Repository and Persistence Rules

## 22.1. Trách nhiệm Repository
- Repository contract phải phục vụ use case / aggregate thật của hệ thống, không tự tạo generic EF repository nếu codebase không cần.
- Write quan trọng phải atomic. Đảm bảo toàn vẹn dữ liệu qua transaction hoặc savepoint khi cần.
- Dùng optimistic concurrency / lock theo pattern hiện tại của service.
- Không giữ database transaction mở khi thực hiện các cuộc gọi HTTP/Kafka ra bên ngoài service nếu có thể tránh.
- Migration phải được review cùng invariant và rollout impact.

## 22.2. Quy chuẩn LINQ và EF Core (Ưu tiên hàng đầu)
Mọi truy vấn dữ liệu thông qua EF Core phải được tối ưu hóa để thực thi hiệu quả tại Database:
- **Thực thi tại Database (Server-side Evaluation)**: Toàn bộ điều kiện lọc (`Where`), sắp xếp (`OrderBy`), gom nhóm (`GroupBy`), đếm (`CountAsync`) phải được biên dịch và chạy trên PostgreSQL.
- **Chiếu dữ liệu (Projection)**: Sử dụng `.Select()` để chỉ tải về các trường dữ liệu cần thiết cho DTO / Query Record, giảm tải bộ nhớ và băng thông mạng.
- **Không theo dõi cho truy vấn đọc (AsNoTracking)**: Bắt buộc sử dụng `.AsNoTracking()` cho toàn bộ các truy vấn chỉ đọc (read-only queries).
- **Thực thi bất đồng bộ đầy đủ**: Luôn dùng các phương thức async (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync`) kèm theo việc propagate `CancellationToken`.
- **Tránh triệt để N+1 Queries**: Khi cần tải aggregate để cập nhật, sử dụng `.Include()` / `.ThenInclude()` một cách chính xác và vừa đủ; đối với truy vấn báo cáo/hiển thị, ưu tiên projection phẳng thay vì include nhiều cấp.
- **Tuyệt đối cấm tải toàn bộ dữ liệu vào bộ nhớ để lọc (No In-Memory Filtering)**: Nghiêm cấm việc gọi `.ToList()` hoặc `.AsEnumerable()` trên bảng dữ liệu rồi mới dùng LINQ to Objects để `Where`, `OrderBy`, hoặc tính toán.
- **Không expose `IQueryable` ra ngoài Repository**: Repository interface tuyệt đối không trả về `IQueryable<T>`. Phải trả về kiểu dữ liệu cụ thể (`Task<T?>`, `Task<IReadOnlyList<T>>`, `Task<PagedResult<T>>`), tránh làm rò rỉ EF Core query execution sang tầng Application hoặc Controller.
- **Không gọi `DbContext` trực tiếp từ Application**: Mọi thao tác đọc/ghi dữ liệu của Application Service bắt buộc phải đi qua Repository interface (`Domain/IRepositories`).

## 22.3. Quy chuẩn Native SQL (Ngoại lệ hẹp)
Native SQL chỉ được chấp nhận như một **ngoại lệ hẹp** khi LINQ / EF Core không thể đáp ứng yêu cầu hiệu năng hoặc cần sử dụng tính năng đặc thù của hệ quản trị cơ sở dữ liệu PostgreSQL.

**Các trường hợp ngoại lệ hợp lệ trong RentalService**:
1. **PostgreSQL Session Advisory Lock**:
   - Sử dụng `pg_try_advisory_lock` và `pg_advisory_unlock` để khóa đơn hàng hoặc người thuê chống race condition đồng thời (triển khai trong `PostgresOrderLockManager`).
2. **PostgreSQL Sequence**:
   - Lấy số nhảy sequence cho mã đơn hàng hoặc outbox message (`SELECT nextval('rental.order_code_seq')`, `nextval('rental.rental_outbox_sequence')` trong `PostgresOrderCodeGenerator`, `OutboxRepository`).
3. **Transaction Savepoint**:
   - Sử dụng `SAVEPOINT` và `ROLLBACK TO SAVEPOINT` cho các thao tác lưu trữ có khả năng phục hồi cục bộ (triển khai trong `InventoryAllocationResultHandler`).
4. **Truy vấn tổng hợp phức tạp (Complex CTE Aggregation)**:
   - Các truy vấn thống kê nhiều CTE phức tạp mà LINQ không dịch tối ưu được (ví dụ: `GetSupplierConfirmationStatisticsAsync` dùng `Database.SqlQuery<T>`).

**Điều kiện bắt buộc khi sử dụng Native SQL**:
- **Ghi rõ lý do**: Phải có chú thích (comment) giải thích rõ ràng tại sao LINQ / EF Core không đáp ứng được yêu cầu.
- **Parameterize 100% dữ liệu đầu vào**: Tuyệt đối không cộng chuỗi SQL thô (string concatenation). Phải sử dụng interpolated string của EF Core (`Database.SqlQuery<T>($"...")`, `ExecuteSqlInterpolatedAsync`) hoặc `NpgsqlParameter` để triệt tiêu nguy cơ SQL Injection.
- **Không chứa quyết định nghiệp vụ trong SQL**: SQL chỉ thực hiện thao tác dữ liệu kỹ thuật, lấy dữ liệu hoặc khóa; toàn bộ quyết định nghiệp vụ và validation phải được thực hiện tại Application Service.
- **Kiểm thử tích hợp bắt buộc**: Mọi đoạn Native SQL phải có integration test chạy thực tế với PostgreSQL để xác minh tính đúng đắn.

---

# 23. Date and Time Rules

Business rental MVP dùng:

```text
Timezone: Asia/Ho_Chi_Minh
```

Phân biệt:

```text
Business Date       // ngày thuê
UTC DateTimeOffset  // event/audit timestamp
```

Không dùng machine local timezone implicit.

Ưu tiên injectable `TimeProvider`.

---

# 24. Security Rules

Mọi task phải kiểm tra:

```text
authentication
authorization
ownership
IDOR
mass assignment
input validation
sensitive field exposure
```

Public DTO không expose SerialNumber, internal AssetCode, suspension/internal notes, concurrency metadata hoặc internal allocation detail nếu không có requirement rõ ràng.

---

# 25. Testing Baseline

## Unit tests
Trọng tâm kiểm thử đặt tại tầng **Application**:
- **Nghiệp vụ cốt lõi**: Cover các business invariants, quy tắc đặt trước (booking lead days), state transitions và workflows do Application Services điều phối.
- **Tính toán tài chính & thời hạn**: Kiểm thử chính xác tiền thuê, tiền cọc, đề xuất khấu trừ, hạn thanh toán, hạn xác nhận của supplier, hạn giữ thiết bị (hold expiry), hạn phản hồi khấu trừ của renter.
- **Validation & Policies**: Kiểm thử các validator và policy nghiệp vụ (`CheckoutRules`, `SubOrderStatusPolicy`, `RentalAuthorizationPolicy`).
- **Idempotency & Quyết định**: Kiểm thử hành vi retry idempotency (không tạo duplicate order/transition khi retry cùng operation), quyền hạn actor (Renter/Supplier/Admin) và chống IDOR.
- **Độc lập và nhanh chóng**: Chạy isolated với unit test doubles (mocks/fakes) cho repository interfaces (`IRentalOrderRepository`, `IOrderLockManager`) và external clients (`IInventoryRentalClient`).

## Integration tests
Trọng tâm kiểm thử đặt tại **Persistence, Concurrency, LINQ, Native SQL và Web API**:
- **Cơ sở dữ liệu & EF Core**: Cover database constraints, khóa ngoại, migrations, JSONB snapshot mapping.
- **LINQ Queries & Projections**: Xác minh các câu truy vấn phức tạp, projection DTO `.Select()`, phân trang, sorting và filtering chạy đúng cú pháp SQL trên PostgreSQL thật.
- **Native SQL & Database Features**: Kiểm thử hoạt động của PostgreSQL advisory locks (`PostgresOrderLockManager`), sequences (`PostgresOrderCodeGenerator`), outbox sequence, savepoints và complex CTE queries (`GetSupplierConfirmationStatisticsAsync`).
- **Concurrency & Locking**: Kiểm thử kịch bản hai request đồng thời tranh chấp tài nguyên (ví dụ checkout cùng thiết bị, đồng thời phản hồi khấu trừ).
- **Transaction Rollback & Outbox/Inbox**: Đảm bảo rollback sạch khi có lỗi hạ tầng; kiểm thử luồng ghi/đọc Outbox và deduplication Inbox.
- **API Endpoints**: Kiểm thử routing, model validation, authentication/claims mapping, ProblemDetails response, IDOR prevention.

## Critical Rental scenarios

```text
successful checkout
double checkout
price changed
insufficient availability
concurrent quantity booking
payment timeout
supplier accept
supplier reject
supplier timeout
not delivered
normal return
early return
overdue
one damaged unit in multi-unit rental
deduction accepted
deduction disputed
duplicate events
dependency temporary failure
```

---

# 26. MVP Explicit Non-goals

Trừ khi Jira Task ghi rõ, không implement:

```text
weekly/monthly pricing engine
dynamic pricing
promotion/coupon
automatic discounts
generic shipping fee engine
rental extension
edit rental after checkout
early-return rental refund
late-return monetary penalty
electronic contract signing
review/rating
incident/breakdown service
automatic asset substitution
bundle/kit engine
StockPool / quantity-only inventory
warehouse movement ledger
organization ownership migration
complex fraud scoring
generic saga framework
event sourcing
```

---

# 27. Current Known Refactor: Inventory

Source hiện tại có thể vẫn còn model cũ:

```text
InventoryAsset
- EquipmentTypeId
- BrandId
- Model
- SpecValues
- AssetRentalRate
```

và public discovery theo physical Asset.

Target MVP:

```text
EquipmentListing
- common/public/commercial fields

InventoryAsset
- physical unit fields
- EquipmentListingId
```

Giá thuê/cọc thuộc Listing-level commercial model.

Public marketplace discovery trả Listing, không trả từng serial physical asset.

Agent không được dựa vào source cũ để kết luận rằng public marketplace vẫn thuê physical Asset.

---

# 28. Current Known Refactor: Rental SRS & Architecture

## 28.1. Rental SRS v1.2 vs Target MVP

Rental SRS v1.2 còn assumption cũ.

Khi implement target MVP, áp dụng override:

### Old

```text
1 Rental = 1 physical equipment
Cart item = physical asset
Rental calls hold(assetId)
```

### MVP Target

```text
1 RentalItem
= EquipmentListing + Quantity + DateRange

Inventory assigns physical Assets internally.
```

### Old roadmap

```text
Phase 3:
system auto-select machine
```

### MVP Target

Auto-select physical asset khi allocation là **Phase 1 core behavior**.

### Old return behavior

```text
returned
→ release reservation
→ calendar free
```

### MVP Target

Nếu damaged:

```text
release rental allocation
+
block damaged physical Asset
```

### Additional MVP invariant

Nếu overdue và chưa actual return:

```text
physical asset must remain unavailable
```

## 28.2. Hiện trạng mã nguồn RentalService: Chuyển dịch nghiệp vụ từ Domain về Application

Trong codebase hiện tại của RentalService, do lịch sử phát triển, vẫn còn một số logic nghiệp vụ nằm rải rác bên trong các Domain Entities và Domain Policies:
- **`Domain/Entities/RentalItem.cs`**: Đang chứa các phương thức tính toán hạn như `CalculateSupplierConfirmationDeadline`, `CalculateConfirmationHoldExpiry`, và các thao tác chuyển trạng thái như `RecordPaid`, `ApplySupplierAccept`, `ApplySupplierReject`, v.v.
- **`Domain/Entities/RentalItem.Settlement.cs`**: Đang chứa logic kiểm tra quyền phản hồi của renter (`ValidateRenterResponse`), các transition giải quyết khấu trừ (`AcceptDeductionByRenter`, `DisputeDeductionByRenter`), và kiểm tra cửa sổ thời gian (`IsRenterResponseOpen`, `NeedsRenterResponseTimeout`).
- **`Domain/Policies/RentalBookingPolicy.cs`**: Đang chứa quy tắc tính toán ngày đặt trước tối thiểu (`MinimumLeadDays`, `EarliestStartDate`, `IsStartDateTooSoon`).

**Quy tắc chuyển dịch bắt buộc cho Agent**:
1. **Không mở rộng refactor ngoài phạm vi**: Agent tuyệt đối **không tự ý mở một cuộc refactor toàn hệ thống** để dọn dẹp các file trên nếu Jira Subtask hiện tại không liên quan đến chúng.
2. **Chuyển dịch khi có task liên quan**: Khi nhận một Jira Subtask có phạm vi chỉnh sửa, mở rộng hoặc thay thế các tính năng liên quan đến các phương thức trên:
   - Phải chuyển toàn bộ các quyết định nghiệp vụ, công thức tính toán và chuyển trạng thái này về **Application Service** (hoặc **Application Policies**).
   - Cập nhật toàn bộ các caller và unit tests tương ứng để kiểm thử thông qua Application Service.
   - Xóa bỏ (delete) implementation cũ trong Domain Entity / Domain Policy trong cùng phạm vi subtask đó để đảm bảo nguyên tắc: **Mỗi hành vi nghiệp vụ chỉ có duy nhất một implementation chính thức**.
---

# 29. How an Agent Must Execute a Jira Subtask

## Step 1 — Read context

Đọc:

1. Jira Task/Subtask;
2. file này;
3. source code service liên quan;
4. SRS/README service liên quan nếu task reference.

## Step 2 — Analyze before coding

Xác định:

```text
Goal
In scope
Out of scope
Affected business rules & invariants (Application Services/Policies)
Affected entities (Domain)
Affected APIs/contracts
Affected persistence (Repositories, LINQ, Migrations)
Affected events
Security impact
Concurrency/idempotency impact
Tests required (Unit at Application, Integration at DB/API)
```

Nếu task yêu cầu plan trước thì liệt kê:

```text
Add
Modify
Delete
Migration
Tests
```

## Step 3 — Preserve boundaries

Không sửa service khác chỉ để task hiện tại dễ implement.

Nếu dependency chưa tồn tại:

- tạo interface/contract/mocks theo contract đã freeze;
- không invent production behavior của service khác.

## Step 4 — Minimal complete implementation

Implementation phải hoàn chỉnh cho scope task ở các layer thực sự bị ảnh hưởng: domain, application, persistence, API, validation, authorization và tests.

Không chỉ tạo endpoint mà bỏ persistence/test nếu acceptance criteria cần end-to-end behavior.

## Step 5 — Verify

Agent phải chạy tối thiểu:

```bash
dotnet build
dotnet test
```

cho solution/service bị ảnh hưởng.

Nếu không chạy được phải ghi rõ command, failure, reason và phần chưa verify.

Không được nói "done" nếu build/test chưa được kiểm chứng.

---

# 30. Parallel Development Rules

## 30.1. Contract-first

Foundation task phải freeze:

```text
domain names
state names
DTO contracts
external interfaces
event schemas
error codes
```

Sau đó feature task dùng mocks/fakes để code độc lập.

## 30.2. Tránh shared god files

Không gom toàn bộ logic vào `RentalService.cs` hoặc `InventoryService.cs`.

Feature riêng nên có folder/class riêng để giảm merge conflict.

## 30.3. Không silent contract change

Nếu subtask cần đổi một frozen contract:

1. dừng phần contract-breaking;
2. document lý do;
3. đề xuất thay đổi;
4. chỉ sửa sau khi task/architecture được cập nhật.

## 30.4. Database migrations

Mỗi feature branch tránh đồng thời chỉnh cùng migration file.

Migration mới phải có timestamp/name riêng và mô tả forward change, compatibility và rollback/forward-fix strategy.

---

# 31. Definition of Done cho một Subtask

Một subtask chỉ được coi là hoàn tất khi các điều phù hợp đã đạt:

- Requirement và acceptance criteria được đáp ứng.
- Không phá domain invariant của tài liệu này.
- Toàn bộ quyết định nghiệp vụ, tính tiền, deadline, quyền hạn và chuyển trạng thái nằm tại Application Service; không đặt trong Domain Entity hay Repository.
- Policy/validator hỗ trợ nghiệp vụ nằm tại Application.
- Repository chỉ làm persistence, sử dụng LINQ tối ưu thực thi tại database (projection `.Select`, `AsNoTracking`, `CancellationToken`, không N+1, không in-memory filtering).
- Không expose `IQueryable` ra ngoài Repository.
- Không truy cập `DbContext` trực tiếp từ Application Service hay Controller.
- Nếu có Native SQL: có lý do giải thích rõ ràng, parameterize tham số chống SQL injection, không chứa logic nghiệp vụ, và có integration test PostgreSQL.
- Tuân thủ các nguyên lý SOLID (SRP cho Services, ISP cho consumer interfaces, LSP cho contract, DIP cho DI).
- Khi chạm vào legacy code có nghiệp vụ ở Domain, đã chuyển về Application và xóa bỏ implementation cũ trong phạm vi task.
- Không scope creep.
- API/contract đúng schema đã thống nhất.
- Authorization/ownership được kiểm tra.
- Validation đầy đủ.
- Idempotency/concurrency được xử lý nếu có side effect.
- Persistence atomic nếu cần.
- Error mapping đúng.
- Tests mới được thêm (Unit test cho Application; Integration test cho Persistence/LINQ/Native SQL/API).
- Existing tests không bị phá.
- Build thành công.
- Không leak secret/sensitive data.
- Migration/config/docs được cập nhật nếu cần.
- Agent ghi rõ limitation hoặc follow-up ngoài scope.

---

# 32. Jira Subtask Prompt Template

```text
TASK:
<ticket + title>

GOAL:
<business outcome>

CONTEXT:
- Read DPRENTAL_PROJECT_AGENT_GUIDE.md first.
- Relevant service: <service>
- Relevant SRS/README: <docs>

IN SCOPE:
- ...
- ...

OUT OF SCOPE:
- ...
- ...

REQUIRED BEHAVIOR:
- ...
- ...

CONTRACTS:
- request:
- response:
- errors:
- events:

DOMAIN INVARIANTS:
- ...

CONCURRENCY / IDEMPOTENCY:
- ...

SECURITY:
- ...

EXPECTED FILE AREAS:
- Domain/Entities/ (chỉ data, relations, mapping)
- Domain/IRepositories/ (repository interfaces, query records, không leak IQueryable)
- Domain/Repositories/ (DbContext, EF Core repos, LINQ, DB operations)
- Application/Services/<Feature>/ (sở hữu toàn bộ nghiệp vụ, workflows, transitions, calculations)
- Application/IServices/<Feature>/ (interfaces nhỏ gọn theo ISP)
- Application/Policies/ (quy tắc và validation hỗ trợ nghiệp vụ)
- Application/DTOs/ (data transfer objects)
- Api/Controllers/ (mỏng, chỉ dispatch sang Application Service)
- tests/Api.Tests/ (Unit tests cho Application; Integration tests cho Database/LINQ/SQL/Concurrency)

ACCEPTANCE CRITERIA:
1. ...
2. ...
3. ...

TEST CASES:
- ...
- ...

DELIVERABLE:
Before coding:
1. summarize current implementation;
2. list impacted files;
3. identify risks/conflicts.

After coding:
1. summarize changes;
2. list migrations/contracts changed;
3. provide build/test result;
4. identify remaining out-of-scope work.
```

---

# 33. Agent Anti-patterns

```text
❌ đổi domain model chỉ vì code cũ tiện hơn
❌ implement future scope "cho đầy đủ"
❌ duplicate commercial data xuống từng Asset nếu Listing đã sở hữu nó
❌ public SerialNumber/AssetCode
❌ cho Rental tự tính physical availability
❌ cho renter chọn AssetId nếu task không yêu cầu
❌ dùng daily count để suy range availability
❌ release damaged asset thành available
❌ để overdue asset trở lại available trước actual return
❌ gọi DB service khác
❌ tạo cross-service foreign key
❌ bỏ idempotency cho payment/allocation flow
❌ hard-code dependency behavior khi contract chưa tồn tại
❌ swallow dependency failure và coi như success
❌ sửa nhiều module không liên quan chỉ để refactor đẹp hơn
❌ đặt workflow, tính toán nghiệp vụ, chuyển trạng thái vào Domain Entity, constructor, computed property, hoặc extension method
❌ tạo "Domain Policy" để lách luật đặt nghiệp vụ ở Domain thay vì đưa vào Application
❌ cho Application Service hoặc Controller truy cập trực tiếp DbContext để né Repository
❌ expose IQueryable ra ngoài Repository interface
❌ tải toàn bộ bảng dữ liệu về bộ nhớ rồi mới lọc hoặc đếm (In-memory filtering gây tràn RAM và N+1)
❌ viết Native SQL chứa logic/quyết định nghiệp vụ hoặc không parameterize tham số đầu vào
❌ vi phạm SRP: tạo God Service hoặc God Repository ôm đồm nhiều nghiệp vụ không liên quan
❌ vi phạm LSP: implementation giả lập thành công hoặc âm thầm trả về tập rỗng thay vì xử lý đúng contract và lỗi
❌ vi phạm ISP: tạo interface máy móc không xuất phát từ nhu cầu thực tế của consumer
```

---

# 34. High-level MVP Success Criteria

MVP core được xem là hoàn chỉnh khi flow sau chạy end-to-end:

```text
Supplier adds serialized physical assets
        ↓
Assets grouped under EquipmentListing
        ↓
Renter searches public listings
        ↓
Renter selects dates + quantity
        ↓
Inventory verifies continuous availability
        ↓
Rental checkout creates atomic holds
        ↓
Renter pays
        ↓
Supplier accepts
        ↓
Equipment is handed over
        ↓
Rental is active
        ↓
Equipment is returned
        ↓
Normal or damage settlement
        ↓
Inventory becomes correctly available/blocked
        ↓
Rental closes
```

Core phải đảm bảo:

```text
no overbooking
no duplicate order/hold on retry
commercial snapshot is immutable
physical assets remain traceable
damaged/overdue assets are protected
service boundaries remain clean
```

---

# 35. Final Rule for Agents

**Không tối ưu cho việc hoàn thành ticket nhanh nhất nếu việc đó phá domain model.**

Nhưng cũng:

**Không xây thêm framework/abstraction chỉ vì "sau này có thể cần".**

Mục tiêu:

```text
Correct MVP
+
clear service boundaries
+
safe concurrency/idempotency
+
testable code
+
parallel-friendly implementation
```

Đây là tiêu chuẩn mặc định cho mọi Jira Task/Subtask của DPRental.
