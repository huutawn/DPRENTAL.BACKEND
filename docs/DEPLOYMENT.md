# Notification dev deployment

The `notification/develop-ci-cd` branch follows `rental/develop-ci-cd`: validate deployment scripts, publish runtime and EF migrator images to GHCR tagged with the full commit SHA, verify package readability, and deploy through the administrator-owned VM entrypoint. Pushes to `notification/develop` do not trigger this workflow.

## VM configuration

The existing `dprental-dev` project lives at `/opt/dprental/dev`. Merge the two services in `deploy/compose.notification.yaml` into its existing `compose.yaml`; keep the existing shared infrastructure. Notification needs both `services` and `egress` networks, because SMTP is outbound to Brevo.

- `.env`: existing Postgres, Auth issuer/audience and internal key, plus `NOTIFICATION_KAFKA_GROUP_ID=notification-service-v1`.
- `notification-smtp.env`: SMTP settings only, root-owned mode 0600. Populate using `deploy/notification-smtp.env.example`; never commit credentials.
- `release.env`: `NOTIFICATION_SERVICE_IMAGE` and `NOTIFICATION_MIGRATOR_IMAGE`, using the repositories shown in `deploy/release.env.example`.
- `/usr/local/libexec/dprental/deploy-notificationservice.sh`: reviewed service deployment script, installed root-owned mode 0755 next to the existing `deploy-common.sh`.
- `/usr/local/sbin/dprental-deploy`: add the `notification` service mapping while preserving existing caller restrictions. The checked-in version preserves frontend/backend runner isolation.

The host entrypoint uses the same backend runner and sudo rule as Rental. It validates trusted files, serializes deployments, backs up PostgreSQL, runs migrations before runtime recreation, waits for health, checks public Gateway and Auth JWKS, and requires anonymous Notification API calls to return 401. Release state is committed only after successful checks; failed mutations require manual recovery rather than automatic schema rollback.

## First deployment and dependencies

The Auth batch endpoint `/api/v1/auth/internal/users/notification-contacts:batch` must exist and accept the existing internal `X-Api-Key`. Its response fields must match `AuthContactClient`. The VM initially lacked it; the isolated dependency branch `auth/fix/notification-vm-contact` applies the existing contact endpoint implementation to the deployed Auth revision, without schema changes.

Gateway must include the Notification REST, negotiate, WebSocket and internal-blocking routes. Its Development downstream must resolve `notification-service:8080` on the VM, even though local workstation development uses port 40992. The administrator-owned VM route file `ocelot.Notification.json` expresses that VM configuration. Existing indexed `Routes__N` environment overrides must be remapped if sorted configuration insertion shifts indices. Keep all unrelated routes unchanged.

Before starting the consumer for the first time, initialize its offsets at the approved cutover. The bootstrap used the current end offsets of `rental.events.v1` and `payment.analytics.v1`, avoiding historical email replay. Never reset an existing active group during routine deployments. Keep the same group across upgrades.

Run the migrator through Compose using `.env` and `release.env`. The EF design-time factory reads `ConnectionStrings__Postgres`; the bundle does not require SMTP, Redis or JWT configuration. Do not pass the database password as a CLI argument.

## Verification

```bash
python3 -m unittest discover -s scripts/tests -p 'test_deploy*.py' -v
dotnet build NotificationService.sln -c Release
dotnet test NotificationService.sln -c Release --no-build
```

Docker targets: `runtime` and `migrator`. Building `migrator` also checks EF model/migration synchronization. Smoke checks cover readiness, anonymous 401, internal 403, Auth contacts and JWT/JWKS. SMTP submissions accepted by Brevo are not proof of recipient delivery; confirm Delivered in provider logs.

The GitHub `dev` environment needs `GHCR_IMAGE_NAMESPACE=huutawn`, repository variable `DEV_DEPLOY_ENABLED=true`, and the online self-hosted runner label `dprental-deploy`. Registry tokens remain on stdin and are not stored with SMTP credentials.
