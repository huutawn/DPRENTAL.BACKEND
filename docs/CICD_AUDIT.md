# Audit và validation CI/CD DP Rental — 25/10/2026

## Phạm vi và bằng chứng nguồn

Đã dùng `git ls-remote --heads origin '*/develop-ci-cd'`, fetch đúng pattern và đọc
nội dung thực tế từng workflow, Dockerfile, Compose local, deployment script và test.
Remote có đúng tám branch; không phát hiện branch bổ sung cùng pattern. Graph hiện
index workspace `platform/develop`, không index tám revision này; đã tham khảo graph,
đọc trực tiếp shell/YAML/config ở worktree để không suy diễn theo index khác branch.

| Branch | Base commit | Workflow đã đọc | Script đã đọc |
| --- | --- | --- | --- |
| auth/develop-ci-cd | ddae95d1d5f37dfeaea931a47cb0a4ebded8bfd3 | authservice-dev.yml | deploy-authservice.sh |
| catalog/develop-ci-cd | 7f7927d55ced8fdaf0d0e23f87a1e308ea7044ca | catalogservice-dev.yml | deploy-catalogservice.sh |
| gateway/develop-ci-cd | f2bffb014dc1d05000c5c8a1270b615b06253006 | gateway-dev.yml | deploy-gateway.sh |
| inventory/develop-ci-cd | feb27731ee03d127b8151913c01a2bc46616a758 | inventoryservice-dev.yml | deploy-inventoryservice.sh |
| media/develop-ci-cd | 17ae7eadddafd06f046768a458aea4d77974a13d | mediaservice-dev.yml | deploy-mediaservice.sh |
| payment/develop-ci-cd | a2c5405b409bd880013cd45b537839aa12b636e2 | paymentservice-dev.yml | deploy-paymentservice.sh |
| recommendation/develop-ci-cd | 22cd480ee85fedf953485fe1acc8193310b747ad | recommendationservice-dev.yml | deploy-recommendationservice.sh |
| rental/develop-ci-cd | 6cbd6862d2cebfc6e753bdd3837aebe6cf19c463 | rentalservice-dev.yml | deploy-rentalservice.sh |

Workflow nằm trong `.github/workflows/`, script trong `scripts/`. Tám worktree detached
ở `/home/tawn/code/dprental-cicd-review/<service>`, ban đầu tách detached để review.
Sau audit, người dùng đã cho phép commit/push từng branch CI/CD để test hosted pipeline;
commit/run cụ thể được ghi trong bản bàn giao. Không sửa
`main`, `*/develop`, business code hoặc Dockerfile. Workspace ban đầu `platform/develop`+có thay đổi chưa commit của người dùng trong submodules/AnalyticsService/architecture;
không reset, stash hay ghi đè. Remote-tracking refs cập nhật bằng fetch, không sửa remote.

## Audit trước refactor

Tất cả workflow chỉ trigger push trên branch CI/CD riêng, không nhận PR/dispatch.
Top-level `contents: read`; publish `contents: read, packages: write`; deploy
`contents: read, packages: read`. Publish dùng hosted Ubuntu 24.04, checkout v4,
Buildx v3, GHCR login v3, build-push v6, context `.`, target riêng và `${{ github.sha }}`.
Namespace là environment variable; API hiện trả `GHCR_IMAGE_NAMESPACE=huutawn`.
Không có job chạy .NET tests trên CI ban đầu; Dockerfile thực hiện restore/publish.

Deploy đều hosted, needs publish, environment dev, concurrency `dprental-dev` với
`cancel-in-progress: false`. Checkout riêng deployment shell rồi truyền username,
job token và nội dung shell qua SSH stdin tới host trong secrets. Shell triển khai
đã chạy Compose local; không cần viết lại thành SSH client. Không tìm thấy ProxyJump,
scp hoặc WireGuard command trong tám deploy path; vị trí network hop thực tế phía VPS
không được chứng minh chỉ bằng code này. Đã loại bỏ dependency SSH/public-host khỏi
deployment YAML, không thay đường SSH quản trị ngoài CI.

| Cấu hình | Xử lý |
| --- | --- |
| DEV_VPS_SSH_PRIVATE_KEY, DEV_VPS_KNOWN_HOSTS | Loại khỏi workflow và hướng dẫn vận hành mới; giữ secret trên GitHub đến sau cutover |
| DEV_VPS_HOST, DEV_VPS_USER, DEV_VPS_PORT | Loại khỏi deployment path; chưa xóa trên GitHub |
| DEPLOY_SSH_PRIVATE_KEY, DEPLOY_VPS_* | Xóa env trung gian, ssh-agent, known_hosts và SSH shell streaming |
| ProxyJump / WireGuard / Tailscale bắt buộc trong job | Không có command trong tám bản cũ; không đưa vào bản mới |
| GHCR_USERNAME, GHCR_TOKEN | Giữ contract; lấy từ actor/job token, truyền stdin, Docker config tạm |
| GHCR_IMAGE_NAMESPACE | Giữ environment variable publish; root cấu hình namespace tương ứng trên VM |
| .env / PEM / Firebase credentials | Giữ trên VM; không mount workspace hoặc đưa lên GitHub |

Tất cả script trước đây ghi `release.env` trước pull, không có host file lock hoặc
recovery marker. Lỗi pull/migration/health có thể để lại release sai so với runtime.
GitHub concurrency không khóa được admin/script thủ công; mặc định không bảo đảm giữ
mọi pending job. Credentials đã có cleanup Docker config, được giữ lại và đưa vào
transaction chung. Không thấy lệnh xóa volumes hoặc auto schema rollback trong tám script.

## Khác biệt và thay đổi theo service

| Service | Images/targets giữ nguyên | Backup/migration và health được giữ hoặc bổ sung |
| --- | --- | --- |
| Auth | dprental-auth runtime, auth-migrator migrator | Thêm full PostgreSQL backup; giữ migrator → runtime healthy, Gateway, JWKS |
| Catalog | catalog runtime + catalog-migrator migrator | Giữ stop → schema catalog backup → migration; giữ internal ready, public categories/equipment-types |
| Gateway | gateway runtime, không migrator | Chỉ pull/recreate Gateway; giữ internal ready, public ready và brands |
| Inventory | inventory runtime + inventory-migrator migrator | Giữ stop → schema inventory backup → migration; internal ready/Gateway; kiểm tra dependencies thực tế gồm Meilisearch |
| Media | media runtime + media-migrator migrator | Giữ schema media backup, SDK/dotnet-ef migrator hiện có, internal ready/Gateway; init MinIO phải exited:0 |
| Payment | payment/runtime, payment-migrator/migrator, payment-integrity/runtime-integrity, payment-integrity-migrator/migrator-integrity | Thêm backup hai DB; giữ Payment migrator → Integrity migrator → healthy IntegrityHost → Payment API → Gateway/JWKS |
| Recommendation | recommendation runtime + recommendation-migrator migrator | Giữ app `--migrate`; thêm full DB backup; healthy/Gateway/JWKS; kiểm tra Kafka |
| Rental | rental runtime + rental-migrator migrator | Thêm full DB backup; giữ migrator → runtime healthy/Gateway/JWKS, kiểm tra dependencies thực tế |

Không đổi migrator implementation, migration code, Dockerfile/build context, volumes,
networks, secrets hoặc business config. Dependency startup trước đây được thay bằng
readiness checks: migration/recreate dùng `--no-deps`, tránh restart service khác.
Các schema `payment`, `recommendation` và shared-DB connection/search path phải đã
được chuẩn bị đúng; script không tự tạo hoặc sửa production database ngoài migrator
đã duyệt. Payment runtime cổng 8080, IntegrityHost 8081 với `/health` như trước.

Đã sửa từng script hiện có, xóa các hàm ghi release trực tiếp và logic login phân tán;
`deploy-common.sh` sở hữu transaction, lock, approval, candidate, credentials và journal.
`dprental-deploy` là boundary đặc quyền thực sự, không phải pipeline thứ hai. Helper,
entrypoint và tests security cùng checksum giữa các branch để phát hành độc lập;
trên VM chỉ có một bộ canonical tại libexec. Không có flag giữ đường SSH cũ trong code.

Tests `.test.sh` cũ (và Inventory `.test.py`) được thay bằng test Python chạy script thật,
mô phỏng đúng biên Docker/HTTP. Tests SSH streaming bị loại vì execution contract đó
đã bỏ; giữ coverage hành vi backup/migration/health và mở rộng failure/permission/race.
README được thay phần CI/CD cũ bằng runbook; tài liệu migration lịch sử vẫn áp dụng
cho kế hoạch thủ công, không phải phê duyệt tự động destructive migration.

## Audit trực tiếp Ubuntu VM

Người dùng bổ sung cho phép `ssh ubuntu-vm` và sửa file trực tiếp. Đã cài code mới tại
`/usr/local/sbin/dprental-deploy`, tám script service cùng helper tại
`/usr/local/libexec/dprental`, namespace root-owned tại `/etc/dprental-deploy/namespace`.
Các đích này trước đó chưa tồn tại; không ghi đè file người dùng. Bộ review/test nguồn
ở `/opt/dprental/cicd-review-20261025`, có manifest checksum file đã cài.
Không cài sudoers, không đổi nhóm runner hoặc quyền Docker/runtime configuration.

Các script VM cũ trong `/opt/dprental/dev/scripts` chưa cutover:
giữ tạm vì activation/deployment chưa được duyệt và VM chưa đáp ứng boundary an toàn.
Workflow trên remote được cập nhật theo phê duyệt commit/push sau audit. Owner
cutover: quản trị viên repo/VM (huutawn/tawn). Điều kiện loại bỏ: private + hardening +
GHCR/job-token access được kiểm chứng + Phase 3 thành công; sau đó lưu archive code cũ
và chuyển mọi caller tới entrypoint. Không cho chạy caller cũ đồng thời đường mới.
Repo không còn hai pipeline song song trong từng worktree đã sửa; push chỉ thay
workflow trên tám branch CI/CD. Đây là giới hạn rollout được yêu cầu, không phải deployment đã thực hiện.

| Hạng mục thực tế | Kết quả |
| --- | --- |
| Repository visibility | PUBLIC, chặn activation |
| dev environment | protection_rules rỗng, branch policy null |
| Runner registration | dprental-deploy-01 online; self-hosted, Linux, X64, dprental-deploy |
| Runner OS account | gha-deploy, không nhóm docker, không có sudo |
| Runtime root directory | /opt/dprental/dev root:root 777 — không an toàn |
| .env / release.env / Compose | owner tawn; .env và release 600, Compose 644 — không đạt root boundary |
| Container state | Tám application runtimes healthy; IntegrityHost healthy |
| Runtime Compose | project dprental-dev; render config hợp lệ |
| Image mapping | Đủ 17 refs service thuộc huutawn, full SHA; không sửa mapping |
| Bind/secrets | Caddyfile/Firebase read-only binds; PEM secrets dưới dev/secrets; phải harden owner/ACL trước activation |
| GHCR metadata qua phiên gh hiện tại | HTTP 403: thiếu read:packages; visibility/package Actions access chưa xác minh |
| Manifest check từ root trên VM | 17/17 unauthorized; không dùng/mượn hoặc tạo credential mới |

Compose local của mỗi service dùng cho phát triển, không phải file production/dev
shared runtime. Đã đọc cả model Compose thật trên VM: có Meilisearch, optional
inventory-cache, Kafka, MinIO/init, hai Postgres và Caddy/Frontend. Không thay các volumes
hay network. Bản Compose render có secrets chỉ được xử lý riêng, không đưa vào báo cáo,
bundle hoặc git; bản lưu tạm local đã xóa, chỉ giữ cấu trúc redacted.

## Validation

PASS bên dưới chỉ nói kiểm tra được nêu, không khẳng định deployment đã thành công.
“Reviewed/BLOCKED” nghĩa code đã review và boundary tests đạt, nhưng quyền runtime
thực tế còn các điều kiện ngăn kích hoạt. Cả tám service **chưa ready để deploy**.

| Service | Workflow valid | Shell valid | Security reviewed | Ready for deploy |
| --- | --- | --- | --- | --- |
| Auth | PASS | PASS | Reviewed / BLOCKED | NO |
| Catalog | PASS | PASS | Reviewed / BLOCKED | NO |
| Gateway | PASS | PASS | Reviewed / BLOCKED | NO |
| Inventory | PASS | PASS | Reviewed / BLOCKED | NO |
| Media | PASS | PASS | Reviewed / BLOCKED | NO |
| Payment | PASS | PASS | Reviewed / BLOCKED | NO |
| Recommendation | PASS | PASS | Reviewed / BLOCKED | NO |
| Rental | PASS | PASS | Reviewed / BLOCKED | NO |

Các lệnh kiểm tra local:

```bash
actionlint -shellcheck= .github/workflows/<service>-dev.yml
shellcheck -x scripts/deploy-*.sh
for script in scripts/deploy-*.sh; do bash -n "$script"; done
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s scripts/tests -p 'test_deploy*.py' -v
git diff --check
```

Actionlint 1.7.12, ShellCheck 0.11.0 (tải từ release chính thức, không cài system);
`.github/actionlint.yaml` khai báo label custom, không bỏ qua label validation.
SC2016 chỉ được annotate tại Perl code/container-shell cần giữ nguyên biến; không
chuyển thành double quotes để lộ/expand biến sai. Python/YAML syntax được kiểm tra.
Đối chiếu YAML parsed trước/sau xác nhận publish không đổi ngoài thêm dependency vào
job script validation; giữ nguyên triggers, runner selectors, dev env, concurrency,
permissions và không còn checkout/SSH trong deploy job.

115 test methods local đã chạy: 14/service, riêng Catalog/Inventory/Media 15/service.
Các test có nhiều subcases: invalid SHA/files/credentials, namespace sai, duplicate
release key, expired/one-use approval, destructive strategy, login/config/pull,
dependency/initializer, stop, schema query bất thường, backup rỗng/hỏng, migrator và
Integrity migrator lỗi, recreate, unhealthy/internal/JWKS/public/DNS lỗi, hai process
tranh cùng lock, state drift từ writer ngoài, không ghi release trước smoke thành công,
không lộ token và cleanup credentials. Docker/HTTP được mô phỏng; không gọi daemon/DB.
Unit security kiểm tra owner/mode/ACL/symlink, allowlist, môi trường sạch cả trước API,
private visibility/timeout và Compose host-escape paths. Không gọi chúng là integration
tests với Docker/DB thật. Kết quả chạy trên Ubuntu được bổ sung trong bàn giao VM.

Phase 2 trên VM đã chạy `docker compose ... config --quiet`, kiểm tra 17 mappings,
container health tám runtime, Gateway internal readiness, Auth JWKS và public GET
`/health/ready`, categories, equipment-types, brands: đạt. Đây là smoke của runtime
đang chạy, không kiểm chứng deployment code mới hoặc compatibility của migration mới.

Đã chạy lại 115 test methods trên Ubuntu VM, cùng bash syntax của chín shell files;
`visudo -cf` xác nhận mẫu sudoers hợp lệ, chưa cài. Entrypoint đã cài từ chối
runtime mode 777; kiểm tra `gha-deploy` vẫn bị sudo từ chối. Đối chiếu snapshot trước/sau
xác nhận 20 container giữ nguyên IDs, StartedAt, image IDs, restart counts; checksum
Compose, `.env`, `release.env` không đổi. Không có migration/restart hoặc sửa DB/volume.

`dotnet build --no-restore -c Release` đã thử trong worktrees: NETSDK1004 thiếu
`obj/project.assets.json` vì chưa NuGet restore. Gateway lúc đầu gọi từ root branch
không có solution (MSB1003), đã gọi lại đúng `Gateway/Gateway.sln`, cũng NETSDK1004.
`dotnet test --no-restore -c Release` (Gateway dùng solution đúng sau đó) không chạy
được test suite hữu ích; một số lệnh exit 0 nhưng không có test results, **không tính PASS**.
Không restore/build Docker hoặc chạy integration .NET/EF/pending-model checks cần
đầy đủ dependencies/DB trong đợt deployment-only này; không thay C# hoặc migrations.
Kết quả build/push GHCR thật sau lần push được duyệt nằm trong bản bàn giao Actions;
không suy ra PASS từ việc YAML giữ nguyên.

Chưa chạy Phase 3 deployment, image pull bằng job token, backup/restore thật, migration,
recreate/restart, image rollback hoặc database recovery. Những hạng mục này cần duyệt
riêng. Xem test plan và recovery cụ thể trong `SELF_HOSTED_DEPLOYMENT.md`.

## Rủi ro và việc còn lại trước cutover

1. Chuyển repo private thực tế, bảo vệ branch/environment/runner và audit mọi workflow.
2. Harden owner/mode/ACL cấu hình và mọi file được privileged path đọc/thực thi; hiện
   wrapper phải từ chối. Không cấp sudoers khi runtime vẫn writable bởi user không tin cậy.
3. Xác minh package visibility và grant Actions access cho đủ 17 packages; kiểm chứng
   token workflow ngắn hạn pull runtime/migrator. Chưa có credential hợp lệ để chứng minh.
4. Review migration theo SHA; rehearsal restore và recovery, nhất là Payment hai DB.
   Full shared DB backup không đồng bộ với Kafka/MinIO; retention/dung lượng quản lý thủ công.
5. Migrate frontend/reset/seed/manual writers sang lock hoặc cấm chạy đồng thời;
   tám workflow an toàn không tự khóa được caller chưa tuân thủ. Wrapper phát hiện release
   drift trước commit để không đè thay đổi của writer ngoài, rồi buộc recovery.
6. Push đã được người dùng duyệt để test hosted pipeline; activation và Phase 3 cần duyệt riêng. Không tự xóa GitHub
   secrets hoặc tắt VPS/WireGuard/Cloudflare/Tailscale trong đợt này.

## Tài liệu nền tảng tham khảo

- [GitHub: Secure use, self-hosted runners](https://docs.github.com/en/actions/reference/security/secure-use)
- [GitHub: GITHUB_TOKEN authentication](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token)
- [GitHub: Container registry authentication và quyền package](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry)
- [GitHub: concurrency](https://docs.github.com/en/actions/concepts/workflows-and-actions/concurrency)

Các nguồn này giải thích rủi ro public runner, token và concurrency; trạng thái repo/VM
ở trên lấy trực tiếp từ API/SSH tại thời điểm audit, không suy ra từ tài liệu.
