#!/usr/bin/env python3
"""Seed thiết bị công trường và nghiệm thu E2E ma trận nghiệp vụ bằng API."""
import argparse
import datetime
import fcntl
import json
import os
import sys
from pathlib import Path

from api import Api, ApiError, RequestBudget
from e2e_scenarios import E2EScenarioRunner, check_schedule_limit, now_vn, today_vn
from fixture import load
from inventory import seed_inventory, verify as verify_inventory
from provision import accounts, catalog, organizations
from state import State, planned_quantities
from warehouse_addresses import run as seed_warehouse_addresses


def read_env_config(environment_file):
    config = {}
    if environment_file and Path(environment_file).is_file():
        for line in Path(environment_file).read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            key, sep, val = line.partition("=")
            if sep:
                val = val.strip()
                if len(val) > 1 and val[0] == val[-1] and val[0] in "\"'":
                    val = val[1:-1]
                config[key.strip()] = val
    # Override with os.environ if present
    for k, v in os.environ.items():
        config[k] = v
    return config


def bootstrap_key(config):
    names = ("AUTH_ADMIN_BOOTSTRAP_CREATION_KEY", "Auth__AdminBootstrap__CreationKey")
    for name in names:
        val = config.get(name)
        if val:
            return val
    return None


def acquire_deployment_lock(lock_dir):
    """Lấy khóa .deployment.lock cho phiên chạy thực thi/tick, không giữ qua nhiều ngày."""
    lock_file = Path(lock_dir) / ".deployment.lock"
    try:
        f = lock_file.open("a")
        fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return f
    except (BlockingIOError, OSError):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--plan", action="store_true", help="Kiểm tra bộ dữ liệu và lịch trình E2E, không gọi API ghi")
    mode.add_argument("--apply", action="store_true", help="Tạo dữ liệu hoặc tiếp tục phiên seed/E2E")
    mode.add_argument("--verify", action="store_true", help="Đọc lại và nghiệm thu toàn bộ bộ seed & E2E")
    mode.add_argument("--timer-tick", action="store_true", help="Chạy theo systemd timer mỗi 15 phút, chỉ xử lý bước đến hạn")
    parser.add_argument("--project", default="dprental-dev", help="Tên project Compose (mặc định dprental-dev)")
    parser.add_argument("--base-url", default="https://dev-api.lazyops.cloud", help="Địa chỉ Gateway")
    parser.add_argument("--state-dir", help="Thư mục state riêng cho môi trường, quyền 0700")
    parser.add_argument("--env-file", help="File .env cấu hình; không in secret ra log")
    parser.add_argument("--deploy-dir", default="/opt/dprental/dev", help="Thư mục triển khai chứa .deployment.lock")
    parser.add_argument("--addresses-only", action="store_true",
                        help="Chỉ seed và kiểm tra địa chỉ kho của owner có inventory")
    parser.add_argument("--request-interval", type=float, default=0.65,
                        help="Khoảng nghỉ tối thiểu giữa hai request, tính bằng giây (mặc định 0,65)")
    args = parser.parse_args()

    project = os.environ.get("DPR_COMPOSE_PROJECT") or args.project
    if project != "dprental-dev" and not project.startswith("dprental-e2e-"):
        print(f"Lỗi: Project '{project}' không hợp lệ. Chỉ chấp nhận 'dprental-dev' hoặc 'dprental-e2e-*'.", file=sys.stderr)
        return 1

    root = Path(__file__).resolve().parent
    state = None
    deploy_lock = None
    try:
        if not 0 <= args.request_interval <= 60:
            raise ValueError("Khoảng nghỉ request phải từ 0 đến 60 giây")

        data, fingerprint = load(root)
        env_config = read_env_config(args.env_file)
        Api(args.base_url)  # Kiểm tra URL trước khi tạo state hoặc gọi API

        # Chế độ --addresses-only
        if args.addresses_only:
            state = State(args.state_dir or root / ".state", args.base_url.rstrip("/"), fingerprint, project=project)
            budget = RequestBudget(args.request_interval)
            clients = {a["key"]: Api(args.base_url, a["email"], a["password"], budget=budget)
                       for a in state.data["accounts"] if a.get("kind") == "owner"}
            report = seed_warehouse_addresses(state, clients, apply=args.apply, verify=args.verify)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0

        # Chế độ --plan
        if args.plan or (not args.apply and not args.verify and not args.timer_tick):
            today = today_vn()
            start_date = today + datetime.timedelta(days=3)
            end_date = start_date + datetime.timedelta(days=2)
            check_schedule_limit(today, end_date, max_days=14)

            plan_report = {
                "trạngThái": "Kế hoạch hợp lệ; chưa gọi API ghi",
                "project": project,
                "tàiKhoản": 15,
                "tổChức": 5,
                "category": 10,
                "brand": 20,
                "equipmentType": 50,
                "listing": 100,
                "asset": sum(planned_quantities()),
                "ngônNgữ": "Tiếng Việt có dấu",
                "ảnh": len(data["imageChecksums"]),
                "fingerprint": fingerprint,
                "e2eScenarios": 15,
                "lịchTrình": {
                    "ngayHienTai": today.isoformat(),
                    "ngayBatDauThueSomNhat": start_date.isoformat(),
                    "ngayKetThucThueDuKien": end_date.isoformat(),
                    "thoiGianToiDa14Ngay": "Hợp lệ (5 ngày <= 14 ngày)"
                }
            }
            print(json.dumps(plan_report, ensure_ascii=False, indent=2))
            return 0

        # Nếu là apply hoặc timer-tick -> Lấy khóa .deployment.lock
        if args.apply or args.timer_tick:
            if Path(args.deploy_dir).is_dir():
                deploy_lock = acquire_deployment_lock(args.deploy_dir)
                if not deploy_lock:
                    print(f"Deployment hoặc thao tác khác đang giữ khóa {args.deploy_dir}/.deployment.lock", file=sys.stderr)
                    return 1

        is_create = args.apply and not (args.state_dir and (Path(args.state_dir) / "state.json").exists())
        state = State(args.state_dir or root / ".state", args.base_url.rstrip("/"), fingerprint,
                      project=project, create=is_create)

        budget = RequestBudget(args.request_interval)

        # Chế độ --timer-tick (chạy theo systemd timer mỗi 15 phút)
        if args.timer_tick:
            clients = {a["key"]: Api(args.base_url, a["email"], a["password"], budget=budget)
                       for a in state.data["accounts"]}
            runner = E2EScenarioRunner(state, root, data, clients, env_config=env_config, project=project)
            e2e_results = runner.run_all(apply=True, verify_only=False)

            waiting_count = sum(1 for r in e2e_results.values() if r.get("status") == "waiting")
            passed_count = sum(1 for r in e2e_results.values() if r.get("status") == "passed")
            failed_count = sum(1 for r in e2e_results.values() if r.get("status") == "failed")

            print(json.dumps({
                "timerTick": "OK",
                "passed": passed_count,
                "waiting": waiting_count,
                "failed": failed_count,
                "allPassed": (passed_count == 15)
            }, ensure_ascii=False, indent=2))
            return 0 if failed_count == 0 else 1

        # Chế độ --apply
        if args.apply:
            b_key = bootstrap_key(env_config)
            clients = accounts(state, root, b_key, budget)
            organizations(state, root, clients)
            _, brand_ids, type_ids = catalog(state, root, data, clients["admin-01"])
            from warehouse_addresses import ADDRESSES, _read_warehouse, _apply_address
            for k, addr in ADDRESSES.items():
                owner_client = clients.get(k)
                if owner_client:
                    w = _read_warehouse(owner_client)
                    if not w or not w.get("hasValidAddress"):
                        _apply_address(owner_client, k, addr, w)
            seed_inventory(state, root, data, clients, brand_ids, type_ids)

            # Chạy E2E Scenarios Runner
            runner = E2EScenarioRunner(state, root, data, clients, env_config=env_config, project=project)
            e2e_results = runner.run_all(apply=True, verify_only=False)

        else:  # --verify
            clients = {a["key"]: Api(args.base_url, a["email"], a["password"], budget=budget)
                       for a in state.data["accounts"]}
            runner = E2EScenarioRunner(state, root, data, clients, env_config=env_config, project=project)
            e2e_results = runner.run_all(apply=False, verify_only=True)

        inventory_report = verify_inventory(state, root, data, clients)

        waiting_count = sum(1 for r in e2e_results.values() if r.get("status") == "waiting")
        passed_count = sum(1 for r in e2e_results.values() if r.get("status") == "passed")
        failed_count = sum(1 for r in e2e_results.values() if r.get("status") == "failed")

        final_summary = {
            "project": project,
            "runId": state.data.get("runId"),
            "datasetId": state.data.get("datasetId"),
            "baselineInventory": inventory_report,
            "e2eScenariosSummary": {
                "total": len(e2e_results),
                "passed": passed_count,
                "waiting": waiting_count,
                "failed": failed_count,
                "milestoneImmediate": "PASSED" if passed_count >= 6 and failed_count == 0 else "PENDING",
                "allCompleted": (passed_count == 15 and waiting_count == 0 and failed_count == 0)
            },
            "scenarios": e2e_results
        }
        print(json.dumps(final_summary, ensure_ascii=False, indent=2))
        return 0 if failed_count == 0 else 1

    except KeyboardInterrupt:
        print("Đã ngắt seed; giữ nguyên state để đối soát và tiếp tục.", file=sys.stderr)
        return 130
    except (ValueError, RuntimeError, OSError, ApiError) as error:
        print(f"Dừng seed: {error}", file=sys.stderr)
        return 1
    finally:
        if state:
            state.close()
        if deploy_lock:
            try:
                deploy_lock.close()
            except OSError:
                pass


if __name__ == "__main__":
    sys.exit(main())
