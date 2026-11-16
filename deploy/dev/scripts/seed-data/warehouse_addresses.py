"""Seed địa chỉ kho mẫu qua API Inventory hiện hữu."""
from api import ApiError, UncertainWrite
from provision import INV, require


ADDRESSES = {
    "owner-01": {"addressDetail": "12 phố Hàng Bông (địa chỉ mẫu)", "provinceCode": "01",
        "wardCode": "00004", "latitude": 21.0358, "longitude": 105.8498},
    "owner-02": {"addressDetail": "25 đường Lê Lợi (địa chỉ mẫu)", "provinceCode": "31",
        "wardCode": "10507", "latitude": 20.9373, "longitude": 106.3146},
    "owner-03": {"addressDetail": "18 đường Nguyễn Văn Linh (địa chỉ mẫu)", "provinceCode": "48",
        "wardCode": "20197", "latitude": 16.0678, "longitude": 108.2208},
    "owner-04": {"addressDetail": "30 đường Lê Lợi (địa chỉ mẫu)", "provinceCode": "79",
        "wardCode": "26743", "latitude": 10.7769, "longitude": 106.7009},
    "owner-05": {"addressDetail": "8 đường Nguyễn Trãi (địa chỉ mẫu)", "provinceCode": "92",
        "wardCode": "31135", "latitude": 10.0342, "longitude": 105.7872},
}


WAREHOUSE_PATH = INV + "/warehouse/me"


def _read_warehouse(client):
    try:
        return client.request("GET", WAREHOUSE_PATH)
    except ApiError as error:
        if error.status == 404:
            return None
        raise


def _owners_with_inventory(state, clients):
    listing_counts = {account["key"]: 0 for account in state.data["accounts"]
                      if account.get("kind") == "owner"}
    for listing in state.data.get("listings", []):
        owner = listing.get("owner")
        if owner in listing_counts:
            listing_counts[owner] += 1

    owners = []
    for key, count in listing_counts.items():
        if not count:
            continue
        if key not in ADDRESSES:
            raise RuntimeError(f"Chưa có địa chỉ mẫu cho {key}; bổ sung trước khi seed")
        client = clients.get(key)
        if client is None:
            raise RuntimeError(f"State thiếu thông tin đăng nhập cho {key}")
        # Xác nhận inventory hiện vẫn có listing; không dựa riêng vào kế hoạch cũ trong state.
        listings = client.pages(INV + "/equipment-listings")
        if listings:
            owners.append((key, client, len(listings)))
    return owners


def _validate_address(record, expected, key):
    require(record is not None and record.get("hasValidAddress") is True,
            f"{key}: địa chỉ kho chưa hợp lệ sau khi đọc lại")
    for field, value in expected.items():
        require(record.get(field) == value, f"{key}: trường {field} không khớp địa chỉ mẫu")


def _apply_address(client, key, address, warehouse):
    payload = dict(address)
    if warehouse is not None:
        payload["expectedVersion"] = warehouse["version"]
    try:
        client.request("PUT", WAREHOUSE_PATH, payload)
    except UncertainWrite as error:
        current = _read_warehouse(client)
        if current and current.get("hasValidAddress"):
            if all(current.get(field) == value for field, value in address.items()):
                return "updated"
            return "skipped"
        raise RuntimeError(f"{key}: chưa xác định kết quả cập nhật; đọc lại trước khi chạy tiếp") from error
    except ApiError as error:
        if error.status != 409:
            raise
        current = _read_warehouse(client)
        if current and current.get("hasValidAddress"):
            return "skipped"
        raise RuntimeError(f"{key}: xung đột phiên bản và kho vẫn thiếu địa chỉ; cần kiểm tra lại") from error

    updated = _read_warehouse(client)
    _validate_address(updated, address, key)
    return "updated"


def run(state, clients, apply=False, verify=False):
    owners = _owners_with_inventory(state, clients)
    counts = {"ownersWithInventory": len(owners), "listings": sum(row[2] for row in owners),
              "updated": 0, "alreadyValid": 0}

    for key, client, _ in owners:
        address = ADDRESSES[key]
        warehouse = _read_warehouse(client)
        if warehouse and warehouse.get("hasValidAddress"):
            counts["alreadyValid"] += 1
            continue
        if verify:
            raise RuntimeError(f"{key}: kho vẫn thiếu địa chỉ hợp lệ")
        if apply:
            outcome = _apply_address(client, key, address, warehouse)
            if outcome == "updated":
                counts["updated"] += 1
            else:
                counts["alreadyValid"] += 1

    counts["mode"] = "verify" if verify else "apply" if apply else "plan"
    if not apply and not verify:
        counts["wouldUpdate"] = counts["ownersWithInventory"] - counts["alreadyValid"]
    return counts
