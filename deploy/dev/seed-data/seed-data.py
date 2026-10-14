#!/usr/bin/env python3
"""Seed thiết bị công trường bằng API; mặc định chỉ lập kế hoạch, không ghi dữ liệu."""
import argparse
import json
import os
import sys
from pathlib import Path
from api import Api, ApiError, RequestBudget
from fixture import load
from inventory import seed_inventory, verify
from provision import accounts, catalog, organizations
from state import State, planned_quantities


def bootstrap_key(environment_file):
    names = ("AUTH_ADMIN_BOOTSTRAP_CREATION_KEY", "Auth__AdminBootstrap__CreationKey")
    values = {os.environ[name] for name in names if os.environ.get(name)}
    if environment_file:
        for line in Path(environment_file).read_text(encoding="utf-8").splitlines():
            key, separator, value = line.strip().partition("=")
            if separator and key in names:
                value = value.strip()
                if len(value) > 1 and value[0] == value[-1] and value[0] in "\"'":
                    value = value[1:-1]
                if value:
                    values.add(value)
    if len(values) > 1:
        raise ValueError("Bootstrap key trong môi trường và file cấu hình không đồng nhất")
    return next(iter(values), None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--plan", action="store_true", help="Kiểm tra bộ dữ liệu, không gọi API ghi")
    mode.add_argument("--apply", action="store_true", help="Tạo dữ liệu hoặc tiếp tục phiên seed")
    mode.add_argument("--verify", action="store_true", help="Đọc lại và nghiệm thu bộ seed")
    parser.add_argument("--base-url", default="https://dev-api.lazyops.cloud", help="Địa chỉ Gateway")
    parser.add_argument("--state-dir", help="Thư mục state riêng cho môi trường, quyền 0700")
    parser.add_argument("--env-file", help="File .env có bootstrap key; không thực thi nội dung file")
    parser.add_argument("--request-interval", type=float, default=0.65,
                        help="Khoảng nghỉ tối thiểu giữa hai request, tính bằng giây (mặc định 0,65)")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    state = None
    try:
        if not 0 <= args.request_interval <= 60:
            raise ValueError("Khoảng nghỉ request phải từ 0 đến 60 giây")
        data, fingerprint = load(root)
        Api(args.base_url)  # Kiểm tra URL trước khi tạo state hoặc gọi API.
        if not args.apply and not args.verify:
            print(json.dumps({"trạngThái": "Bộ dữ liệu hợp lệ; chưa ghi API", "tàiKhoản": 15,
                "tổChức": 5, "category": 10, "brand": 20, "equipmentType": 50, "listing": 100,
                "asset": sum(planned_quantities()), "ngônNgữ": "Tiếng Việt có dấu",
                "ảnh": len(data["imageChecksums"]), "fingerprint": fingerprint}, ensure_ascii=False, indent=2))
            return 0
        state = State(args.state_dir or root / ".state", args.base_url.rstrip("/"), fingerprint, create=args.apply)
        if args.apply:
            clients = accounts(state, root, bootstrap_key(args.env_file), RequestBudget(args.request_interval))
            organizations(state, root, clients)
            _, brand_ids, type_ids = catalog(state, root, data, clients["admin-01"])
            seed_inventory(state, root, data, clients, brand_ids, type_ids)
        else:
            budget = RequestBudget(args.request_interval)
            clients = {a["key"]: Api(args.base_url, a["email"], a["password"], budget=budget) for a in state.data["accounts"]}
        report = verify(state, root, data, clients)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        print(f"Thông tin đăng nhập lưu tại {state.path}; không chia sẻ file này công khai.")
        return 0
    except KeyboardInterrupt:
        print("Đã ngắt seed; giữ nguyên state để đối soát và tiếp tục.", file=sys.stderr)
        return 130
    except (ValueError, RuntimeError, OSError, ApiError) as error:
        print(f"Dừng seed: {error}", file=sys.stderr)
        return 1
    finally:
        if state:
            state.close()


if __name__ == "__main__":
    sys.exit(main())
