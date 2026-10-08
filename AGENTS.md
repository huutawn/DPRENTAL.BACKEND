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

Mọi state transition phải kiểm tra current state, actor, preconditions, idempotency và side effects.

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

Nên consume domain/integration events, không nhét logic gửi notification/report vào domain aggregate.

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

Các service .NET theo structure hiện tại:

```text
<Service>/
  src/
    Common/
    Domain/
    Application/
    Api/
  tests/
```

## Domain

Chứa Entities/Aggregates, ValueObjects, Policies, domain state transitions và repository contracts khi phù hợp.

Domain không phụ thuộc HTTP, controller, EF-specific API, Kafka client, Redis hoặc external service SDK.

## Application

Chứa use cases/services, DTOs, validation, orchestration, external client abstractions và authorization/application policies.

Không nhét tất cả nghiệp vụ vào một `RentalService.cs` khổng lồ.

Ưu tiên:

```text
Application/
  Cart/
  Checkout/
  Orders/
  SupplierDecision/
  Handover/
  Returns/
  Settlement/
  Queries/
```

## API

Chứa controllers/endpoints, authentication wiring, ProblemDetails mapping, dependency registration và hosted/background workers theo pattern project.

Controller phải mỏng.

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

- Repository contract phải phục vụ aggregate/use case thật.
- Không tự tạo generic EF repository nếu codebase không cần.
- Query dữ liệu lớn phải pagination.
- Write quan trọng phải atomic.
- Dùng optimistic concurrency/lock theo pattern hiện tại của service.
- Không giữ DB transaction mở khi gọi HTTP service khác nếu có thể tránh.
- Migration phải được review cùng invariant và rollout impact.

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

Cover domain invariants, state transitions, money calculations, date calculations, validation và idempotency decision logic.

## Integration tests

Cover database constraints, repository query, concurrency, HTTP endpoint auth, transaction rollback và Inbox/Outbox khi liên quan.

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

# 28. Current Known Refactor: Rental SRS

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
Affected domain invariant
Affected entities
Affected APIs/contracts
Affected persistence
Affected events
Security impact
Concurrency/idempotency impact
Tests required
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
- Không scope creep.
- API/contract đúng schema đã thống nhất.
- Authorization/ownership được kiểm tra.
- Validation đầy đủ.
- Idempotency/concurrency được xử lý nếu có side effect.
- Persistence atomic nếu cần.
- Error mapping đúng.
- Tests mới được thêm.
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
- Domain/
- Application/<Feature>/
- Api/
- tests/

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
