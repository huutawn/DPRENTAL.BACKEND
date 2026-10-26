# Vận hành CI/CD self-hosted — DP Rental dev

Ngày audit: 25/10/2026. Phạm vi: 8 branch `*/develop-ci-cd`; không thay business logic.
Đọc kèm `CICD_AUDIT.md`. Các lệnh deploy/restore/cấp quyền dưới đây là hướng dẫn
cho đợt kích hoạt đã được phê duyệt, không phải các lệnh đã thực thi trong refactor.

## Trạng thái deployment

Runner deploy chạy trên Ubuntu VM. Workflow build/publish dùng GitHub-hosted runner; deploy được bật khi repository variable `DEV_DEPLOY_ENABLED=true` và push lên nhánh service `develop-ci-cd`. Đặt variable thành `false` để tạm ngừng deploy.

Không còn kiểm tra visibility Private hay approval file theo từng SHA. Hosted policy vẫn xác minh image GHCR có thể đọc; wrapper giữ allowlist service, SHA 40 ký tự, kiểm tra quyền Compose, rồi script backup/migration/health theo policy service.

`CICD_AUDIT.md` là snapshot audit ban đầu; một số nhận xét trạng thái kích hoạt trong đó không còn là trạng thái hiện tại.

## Ranh giới đặc quyền và cài đặt

Entry point duy nhất được phép cấp sudo: `/usr/local/sbin/dprental-deploy`.
Nó chỉ nhận hai arguments: service allowlist và SHA hex thường dài 40 ký tự.
Username/token nhận qua hai dòng stdin. Không nhận đường dẫn, image, shell hoặc option
từ workflow. Python chạy isolated; shell được exec với environment mới, PATH cố định,
xóa environment trước cả TLS/API preflight, không giữ `BASH_ENV`, `SSL_CERT_FILE`,
`DOCKER_HOST`, Compose overrides hoặc Docker config của runner.

Shell của từng service và `deploy-common.sh` cài tại `/usr/local/libexec/dprental`,
do root sở hữu, runner không được ghi. Không chạy `sudo bash` từ Actions checkout.
Wrapper kiểm tra owner/mode, symlink và POSIX ACL của bản thân, thư mục code,
`/etc/dprental-deploy`, toàn bộ cây `/opt/dprental/dev` và các đường dẫn cha.
Hai thư mục cấu hình phải root-only 0700. Không để root script trong thư mục runner
có thể thay thế bằng rename. Docker executable/plugin do quản trị viên quản lý.

Việc harden cấu hình runtime phải là đợt riêng: lưu metadata/backup cấu hình, kiểm tra
quyền PEM/Firebase trong container, các file mount, scripts seed/reset, backups và
thư mục `caddy-transfer` trước khi đổi owner/mode. Không áp dụng chmod/chown đệ quy
mù quáng; thay owner key có thể ảnh hưởng quyền đọc của UID ứng dụng. Không di chuyển,
xóa hoặc sửa volumes. Có thể lưu các archive không phải runtime ra vùng backup quản trị.
Không sửa nội dung `.env`, Compose hoặc release để né kiểm tra của wrapper.

Compose preflight chỉ render vào bộ nhớ, không in secrets: từ chối privileged,
capabilities bổ sung, host namespaces, devices, API socket, volumes_from, bind mount
ghi được, bind/socket/path không đáng tin, volume driver/options nguy hiểm. Kiểm tra
cả options của named volumes hiện hữu. Mount read-only vẫn cần nguồn tin cậy;
Docker socket read-only vẫn nguy hiểm và bị từ chối vì không phải regular file.
Các secrets/configs được kiểm tra đường dẫn. Compose/config từ admin vẫn là trusted
code; không cho workflow cập nhật các file đó. Wrapper không tự sửa quyền hay volume.

Sau review code, cài từng script đã duyệt bằng `install -o root -g root -m 0755` vào
libexec; helper dùng 0644. Không cho workflow tự cập nhật bộ code đặc quyền. Tám branch
có cùng bản helper/entrypoint vì được phát hành độc lập; kiểm tra checksum giống nhau
khi gom gói cài. Không cài tám bản khác nhau vào cùng đường dẫn một cách tùy ý.
File `/etc/dprental-deploy/namespace` chỉ chứa `huutawn`, owner root, mode 0600.

Sudoers đã được cài theo phê duyệt người dùng; bản nguồn là
`scripts/dprental-deploy.sudoers`:

```sudoers
Defaults:gha-deploy env_reset, !setenv
gha-deploy ALL=(root) NOPASSWD: NOSETENV: /usr/local/sbin/dprental-deploy
```

Entry point tự giới hạn arguments; sudoers không cho thêm command khác. Dùng
`visudo -cf` kiểm tra file khi cài lại root:root 0440. Lần này đã chạy cả `visudo -cf` và `visudo -c`. Chạy `sudo -l -U gha-deploy`
để xác nhận quyền hiệu lực, kể cả sudoers từ nhóm khác. Không thêm runner vào nhóm docker,
không cấp quyền socket, không cấp `NOPASSWD: ALL`.

## Runner online và GHCR

```bash
gh api repos/huutawn/DPRENTAL.BACKEND --jq '{private,visibility}'
gh api repos/huutawn/DPRENTAL.BACKEND/actions/runners \
  --jq '.runners[] | {name,status,labels:[.labels[].name]}'
ssh ubuntu-vm systemctl status actions.runner.huutawn-DPRENTAL.BACKEND.dprental-deploy-01.service
```

Runner: `dprental-deploy-01`, user `gha-deploy`, directory `/opt/actions-runner`,
labels `self-hosted`, `linux`, `x64`, `dprental-deploy` (API có thể hiển thị Linux/X64).
SSH chỉ phục vụ quản trị/kiểm tra ngoài pipeline, không là dependency của deployment.

Workflow publish dùng `packages: write`; deployment dùng `packages: read` và
Token `GITHUB_TOKEN` của job có hạn đến khi job
kết thúc (tối đa 24h theo GitHub); không tạo PAT mới hoặc lưu token lâu dài trên VM.
GHCR packages phải cấp repository này Actions access cho cả runtime/migrator; Payment
cần bốn packages. Build dùng metadata source như trước. Việc repo private không tự đổi
visibility của package; kiểm tra từng package và quyền kế thừa riêng.

Token chỉ đi stdin tới wrapper rồi Docker `--password-stdin`; Docker config tạm 0700,
bị xóa khi script exit/signal được xử lý, kể cả login thất bại. SIGKILL/mất điện có thể
để lại config tạm; admin xử lý thư mục tạm cũ sau khi xác nhận không còn deployment,
token job đã hết hạn. Không log credentials hoặc dùng `set -x`. Không dùng token đăng ký
runner làm GHCR token. Manifest check ban đầu của root không có credential bị unauthorized; đây không phải
bằng chứng image không tồn tại. Hosted preflight mới kiểm tra quyền đọc bằng job token; Docker pull trên VM chưa chạy.

## Deployment

Khi repository variable `DEV_DEPLOY_ENABLED=true`, push vào nhánh service `develop-ci-cd` sẽ tự chạy deployment sau publish. Đặt variable thành `false` để ngừng deploy. Mỗi service vẫn xác thực SHA, kiểm tra Compose và image, backup/migration theo policy của service, rồi kiểm tra health.

## Release state, migration, backup và quan sát

Một `flock` trên `.deployment.lock` bao trùm toàn bộ transaction; Actions cùng group
`dprental-dev`, `cancel-in-progress: false`. GitHub concurrency mặc định có thể thay
pending job, không bảo đảm FIFO hoặc mọi revision đều deploy. Theo dõi run bị hủy/chờ;
không thêm queue option mới vì actionlint 1.7.12 chưa hỗ trợ. Khi bật Phase 3,
chạy lần lượt từng service; hai job trong lượt push đầu đã bị thay thế khi pending. Các công cụ seed/reset/frontend cũ
chưa dùng lock này: không được chạy chúng đồng thời, phải audit/migrate trước cutover
toàn platform. File lock chỉ ngăn các caller tuân thủ giao thức, không thể khóa admin
cố tình sửa tay. Trong tám workflow đã refactor không có hai writer đồng thời.

Journal `.deployments/<service>-<SHA>.<attempt>/` có `previous.env`, `candidate.env`,
`phase`, `events`, `result`, và backup khi service cần. Snapshot dưới lock;
candidate chỉ đổi image của service, giữ nguyên namespace, tag SHA và mapping khác.
Compose validation và pull tất cả image đích xảy ra trước stop/migration/recreate.
Dependencies phải sẵn sàng; script không tự khởi động/recreate dependencies không liên quan.
Migrate dùng `--no-deps --pull never`; runtime dùng thêm `--no-build`.

Catalog/Inventory giữ stop → schema backup → migration → recreate. Media giữ schema
backup → migration, kiểm tra `media-minio-init` đã exited:0. Auth/Rental/Recommendation
thêm full database backup PostgreSQL dùng chung; Payment backup cả PostgreSQL dùng
chung và `integrity-postgres` trước Payment migrator → Integrity migrator → healthy
IntegrityHost → Payment API. Dùng `pg_dump -Fc`, kiểm tra không rỗng và `pg_restore --list`.
Đây là kiểm tra archive, không phải restore rehearsal. Backups full shared database có
dữ liệu nhiều module; không restore toàn bộ lên DB đang có hoạt động của service khác.
Backup online không tạo snapshot đồng bộ hai DB hoặc Kafka/MinIO; cần rehearsal riêng,
đặc biệt với Payment. Không tự rollback schema và không tự xóa backups/images/volumes.

Container phải healthy, giữ probes readiness/JWKS và Catalog anonymous GET hiện có.
IntegrityHost có health riêng trước Payment API. Gateway internal readiness được
kiểm tra trước public smoke; public thất bại vẫn chặn ghi success. DNS/proxy resolution,
network/TLS/timeout và HTTP/upstream được chẩn đoán riêng; lỗi public không tự chứng minh
ứng dụng lỗi. Không dùng `/` để kiểm tra API. Schema/runtime có thể đã đổi khi smoke lỗi.

```bash
sudo journalctl -u actions.runner.huutawn-DPRENTAL.BACKEND.dprental-deploy-01.service -n 100
sudo cat /opt/dprental/dev/.deployment-pending
sudo docker compose --project-name dprental-dev --project-directory /opt/dprental/dev \
  --file /opt/dprental/dev/compose.yaml --env-file /opt/dprental/dev/.env \
  --env-file /opt/dprental/dev/release.env ps
curl --fail --max-time 15 https://dev-api.lazyops.cloud/health/ready
```

Logs ứng dụng: admin dùng Compose `logs --tail 100 <service>`; giữ logs có khả năng chứa
dữ liệu nhạy cảm trong phiên quản trị. Không dump `.env` hay rendered Compose vào Actions.

## Failed deployment, rollback image và restore backup

Lỗi trước mutation giữ nguyên release/runtime, ghi result thất bại và gỡ pending.
Lỗi từ mutation trở đi giữ `.deployment-pending`, chặn toàn bộ deployment tiếp theo.
`release.env` vẫn là release thành công gần nhất; runtime/DB có thể khác nó. Không được
coi file này là mô tả runtime chính xác sau lỗi. Crash lúc commit có thể đã rename release
nhưng chưa ghi success; pending vẫn buộc admin đối soát. Không tự retry migration mù quáng.

Admin giữ khóa `.deployment.lock` trong suốt recovery (dùng `flock` cho shell quản trị),
đọc journal, đối chiếu Docker image/health, migration history và PostgreSQL. Nếu chỉ public
smoke lỗi, kiểm tra DNS → Tunnel → Caddy → Gateway, giữ runtime lành và chạy lại các GET
read-only; quyết định chấp nhận candidate phải do admin ghi nhận. Chỉ gỡ pending sau khi
đã xác nhận release/runtime/database tương ứng và có biên bản recovery. Không xóa journal.

Rollback image chỉ sau khi xác nhận schema mới tương thích image cũ: lấy **các biến
service đích** từ `previous.env` vào file candidate recovery, ghép với release hiện tại;
không copy toàn bộ snapshot cũ đè release của service khác. Render Compose, kiểm tra image
cũ có sẵn/được phép pull, rồi `up -d --no-deps --force-recreate --pull never --no-build`
cho đúng runtime (Payment: IntegrityHost trước Payment API), không gọi migrator. Chạy
health/JWKS/public smoke rồi atomic rename release dưới cùng lock. Đây là thao tác
deployment cần phê duyệt riêng; không có nút rollback schema tự động.

Nếu schema không tương thích hoặc dữ liệu bị ảnh hưởng: ưu tiên fix-forward; nếu phải
restore, đóng luồng ghi có liên quan, giữ backup hiện trạng điều tra, phục hồi archive
vào database cách ly trước, kiểm tra TOC, migration history, số liệu và integration.
Chỉ khôi phục production/dev DB đang chạy khi đã phê duyệt kế hoạch, downtime và phạm vi
dữ liệu. Catalog/Inventory/Media dùng schema backup; chú ý dependencies ngoài schema.
Payment cần đối soát hai DB, ledger/evidence/keys/outbox và các event đã publish; không
restore riêng một DB hoặc reset Kafka/MinIO để “đồng bộ”. Không cung cấp/thi hành lệnh
`pg_restore --clean` tự động, EF database downgrade, `down -v`, xóa volumes.

## Kế hoạch Phase 3 và ngừng VPS

Trình tự triển khai đề xuất: Auth → Gateway → Catalog → Media → Inventory → Rental →
Payment → Recommendation; điều chỉnh khi dependency thực tế yêu cầu. Với mỗi service:
kiểm tra quyền GHCR cho đủ variants, backup/rehearsal, release state,
chạy một deployment, xác nhận healthy/internal/public, đối chiếu image SHA và chỉ biến
release liên quan đổi, quan sát logs/resource, diễn tập failure/recovery trước service kế.
Không kích hoạt đồng loạt tám workflow để thử runner có RAM khoảng 5GB.

SSH quản trị Tailscale và public Cloudflare Tunnel/Caddy giữ nguyên. Chỉ dừng VPS/WireGuard
sau khi tám workflow ổn định, quyền package được chứng minh bằng job token, recovery đã
diễn tập và mọi consumer ngoài phạm vi (đặc biệt frontend/scripts thủ công) được audit.
Sau đó mới duyệt loại bỏ GitHub SSH secrets và tắt hạ tầng cũ. Không xóa secret hoặc
thay DNS/Tunnel trong đợt này.
