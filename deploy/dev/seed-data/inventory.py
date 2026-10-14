"""Listing theo owner, asset theo serial; kiểm chứng nghiệp vụ qua API."""
import time
from api import ApiError, UncertainWrite, public_image
from provision import AUTH, CAT, INV, MARKER, ORG_ACTIVE, ORG_SUPPLIER, TYPE_PUBLISHED, create, equal, match, require
from state import atomic_json


def rental_rate(plan, model):
    """Use the run's explicitly approved price, otherwise the fixture price."""
    rate = model["dailyRate"]
    result = plan.get("rentalRate", {"dailyRate": rate, "weeklyRate": rate * 6,
                                   "monthlyRate": rate * 22, "currency": "VND"})
    require(set(result) == {"dailyRate", "weeklyRate", "monthlyRate", "currency"},
            "Giá thuê trong kế hoạch không đủ hoặc thừa trường")
    require(result["currency"] == "VND" and all(
        isinstance(result[k], (int, float)) and not isinstance(result[k], bool)
        and 0 < result[k] < float("inf") for k in ("dailyRate", "weeklyRate", "monthlyRate")),
        "Giá thuê trong kế hoạch không hợp lệ")
    return dict(result)


def seed_inventory(state, root, data, clients, brand_ids, type_ids):
    for index, plan in enumerate(state.data["listings"]):
        equipment = data["types"][index // 2]
        model = equipment["models"][index % 2]
        owner = clients[plan["owner"]]
        type_id, version = type_ids[equipment["key"]]
        name = f"{equipment['name']} {model['brandName']} {model['model']} · Mẫu {index+1:03}"
        photos = [state.upload(owner, root / image, equipment["key"] + f":{i}")
                  for i, image in enumerate(equipment["images"])]
        payload = {"equipmentTypeId": type_id, "brandId": brand_ids[model["brand"]],
            "name": name, "description": equipment["description"] + " Cấu hình: " + model["market"] + ". " + MARKER,
            "model": model["model"], "serialNumber": plan["serials"][0],
            "specValues": model["specValues"], "schemaVersion": version,
            "manufactureYear": model["manufactureYear"], "mediaIds": photos, "thumbnailMediaId": photos[0],
            "rentalRate": rental_rate(plan, model)}
        validation = owner.request("POST", CAT + f"/equipment-types/{type_id}/specifications/validate",
                                   {"schemaVersion": version, "values": model["specValues"]})
        require(validation.get("isValid", validation.get("valid", False)), f"Thông số {name} không hợp lệ")
        def lookup():
            row = match(owner.pages(INV + "/equipment-listings", q=name), "name", name)
            return owner.request("GET", INV + f"/equipment-listings/{row['id']}") if row else None
        def validate(record):
            expected = {k: v for k, v in payload.items() if k not in ("serialNumber", "mediaIds")}
            expected["ownerUserId"] = next(a["id"] for a in state.data["accounts"] if a["key"] == plan["owner"])
            equal(record, expected, plan["key"])
            require([m["id"] for m in record["media"]] == photos, "Ảnh listing không khớp")
        listing = create(state, "listing:" + plan["key"], owner, INV + "/equipment-listings", payload, lookup, validate)
        path = INV + f"/equipment-listings/{listing['id']}"
        assets = owner.pages(path + "/assets")
        existing = {a["serialNumber"]: a for a in assets}
        require(len(existing) == len(assets), "Danh sách asset có serial trùng")
        require(set(existing) <= set(plan["serials"]), "Listing có asset ngoài bộ seed; không tự sửa")
        for serial in plan["serials"]:
            if serial in existing:
                equal(existing[serial], {"manufactureYear": model["manufactureYear"], "acceptingBookings": True}, serial)
                continue
            def asset_lookup():
                operation = state.data["operations"].get("asset:" + serial)
                if operation and operation["status"] == "pending":
                    existing.update({a["serialNumber"]: a for a in owner.pages(path + "/assets")})
                return existing.get(serial)
            create(state, "asset:" + serial, owner, path + "/assets", {"serialNumber": serial}, asset_lookup,
                   lambda r: equal(r, {"serialNumber": serial, "manufactureYear": model["manufactureYear"],
                                       "acceptingBookings": True}, serial), accept_response=True)
        require(len(owner.pages(path + "/assets")) == plan["quantity"], "Tổng asset không đúng kế hoạch")
        if listing["status"] == "Draft":
            try:
                owner.request("PATCH", path, {"status": "Active"})
            except UncertainWrite:
                require(lookup()["status"] == "Active", "Chưa rõ kết quả kích hoạt listing")
        require(lookup()["status"] == "Active", "Listing không Active")
        print(f"Kho: {name}, {plan['quantity']} thiết bị vật lý")


def wait_for(action, description, attempts=18):
    error = None
    for attempt in range(attempts):
        try:
            return action()
        except (ApiError, RuntimeError, OSError, ValueError) as caught:
            error = caught
            if isinstance(caught, ApiError) and caught.status not in (404, 429, 502, 503, 504):
                raise
            if attempt < attempts - 1:
                time.sleep(5)
    raise RuntimeError(f"Hết thời gian chờ {description}: {error}") from error


def denied(action, statuses, description):
    try:
        action()
    except ApiError as error:
        require(error.status in statuses, f"{description}: mã từ chối không đúng")
    else:
        raise RuntimeError(f"{description}: API không từ chối truy cập")


def verify_boundaries(state, data, clients):
    user, outsider = clients["user-01"], clients["owner-02"]
    denied(lambda: user.request("GET", AUTH + "/admin/users/roles"), {403}, "User đọc quản trị tài khoản")
    denied(lambda: user.request("GET", CAT + "/admin/categories"), {403}, "User đọc quản trị Catalog")
    listing_id = state.data["operations"]["listing:ct-001"]["id"]
    path = INV + f"/equipment-listings/{listing_id}"
    denied(lambda: user.request("GET", path, authenticated=False), {401}, "Ẩn danh đọc listing nội bộ")
    denied(lambda: outsider.request("GET", path + "/assets"), {403, 404}, "Owner đọc asset của owner khác")
    equipment = data["types"][0]
    type_id = state.data["operations"]["type:CT_CORDLESS_DRILL"]["id"]
    values = dict(equipment["models"][0]["specValues"])
    missing = next(f["key"] for f in equipment["schema"] if f["required"])
    values.pop(missing)
    result = clients["owner-01"].request("POST", CAT + f"/equipment-types/{type_id}/specifications/validate",
                                         {"schemaVersion": 1, "values": values})
    require(not result.get("isValid", result.get("valid", True)), "API chấp nhận thông số thiếu trường bắt buộc")


def verify(state, root, data, clients):
    admin = clients["admin-01"]
    accounts_by_key = {a["key"]: a for a in state.data["accounts"]}
    seen_serials, seen_ids, media_urls = set(), set(), set()
    catalog_counts = {}
    for entity, expected in (("categories", 10), ("brands", 20), ("equipment-types", 50)):
        records = admin.pages(CAT + "/admin/" + entity)
        owned_ids = {op["id"] for key, op in state.data["operations"].items()
                     if key.startswith({"categories": "category:", "brands": "brand:", "equipment-types": "type:"}[entity])}
        matched = [r for r in records if r["id"] in owned_ids]
        require(len(matched) == expected, f"Số lượng {entity} không đạt {expected}")
        for row in matched:
            record = admin.request("GET", CAT + f"/admin/{entity}/{row['id']}")
            if entity == "equipment-types":
                require(record["status"] == TYPE_PUBLISHED, "Equipment type chưa publish")
                schema = admin.request("GET", CAT + f"/equipment-types/{row['id']}/spec-schemas/{record['currentSchemaVersion']}")
                require(schema["status"] == "Published", "Schema chưa publish")
                fixture = next(t for t in data["types"] if "CT_" + t["key"].replace("-", "_").upper() == record["code"])
                equal(schema, {"definition": {"fields": fixture["schema"]}}, record["code"])
                media_urls.update(m["url"] for m in record["media"])
            else:
                require(record["isActive"], "Category/brand không hoạt động")
                media_urls.add(record["imageUrl" if entity == "categories" else "logoUrl"])
        catalog_counts[entity] = len(matched)
    for account in state.data["accounts"]:
        client = clients[account["key"]]
        me = client.request("GET", AUTH + "/users/me")
        equal(me, {"email": account["email"], "fullName": account["fullName"], "isProfileCompleted": True,
                   "phoneNumber": account["phoneNumber"], "dateOfBirth": account["dateOfBirth"]}, account["key"])
        require(bool(me.get("avatarMediaId")), "Tài khoản thiếu avatar")
        require(("admin" in me["roles"]) == (account["kind"] == "admin"), "Role quản trị không đúng loại tài khoản")
        require(bool(me.get("avatarUrl")), "Tài khoản thiếu URL avatar")
        media_urls.add(me["avatarUrl"])
        if account["kind"] == "owner":
            require("inventory.asset.manage" in me["permissions"], "Owner thiếu quyền kho")
            org = client.request("GET", AUTH + "/organizations/mine")
            require(org["status"] == ORG_ACTIVE and org["type"] == ORG_SUPPLIER, "Tổ chức không Active Supplier")
            require(org["businessLicenseDocumentMediaId"] and org["logoMediaId"], "Tổ chức thiếu hồ sơ/ảnh")
            media_urls.add(org["logoUrl"])
            # Giấy phép là private: không được cấp content công khai.
            try:
                admin.request("GET", f"/api/v1/media/{org['businessLicenseDocumentMediaId']}/content", authenticated=False)
            except ApiError as error:
                require(error.status in (403, 404), "Quyền truy cập giấy phép không đúng")
            else:
                raise RuntimeError("Giấy phép demo bị công khai")
    for index, plan in enumerate(state.data["listings"]):
        owner = clients[plan["owner"]]
        operation = state.data["operations"].get("listing:" + plan["key"])
        require(operation and operation["status"] == "done", "Listing chưa có receipt thành công")
        listing_id = operation["id"]
        listing = owner.request("GET", INV + f"/equipment-listings/{listing_id}")
        model = data["types"][index // 2]["models"][index % 2]
        equal(listing, {"ownerUserId": accounts_by_key[plan["owner"]]["id"], "status": "Active",
                       "totalQuantity": plan["quantity"], "specValues": model["specValues"],
                       "model": model["model"], "manufactureYear": model["manufactureYear"]}, plan["key"])
        equal(listing["rentalRate"], rental_rate(plan, model), plan["key"])
        assets = owner.pages(INV + f"/equipment-listings/{listing_id}/assets")
        serials = {a["serialNumber"] for a in assets}
        require(serials == set(plan["serials"]) and len(assets) == plan["quantity"], "Asset thiếu, thừa hoặc serial không khớp")
        require(not seen_serials & serials, "Serial trùng giữa các listing")
        seen_serials.update(serials)
        seen_ids.add(listing_id)
        require(len(listing["media"]) == 2 and listing["thumbnailMediaId"] == listing["media"][0]["id"], "Listing thiếu bộ ảnh/thumbnail")
        media_urls.update(m["url"] for m in listing["media"])
        def public_lookup():
            public = admin.request("GET", INV + f"/public/equipment-listings/{listing_id}", authenticated=False)
            equal(public, {"model": model["model"], "specValues": model["specValues"]}, plan["key"])
            return public
        wait_for(public_lookup, "listing công khai")
    for owner_key in (f"owner-{i:02}" for i in range(1, 6)):
        rows = clients[owner_key].pages(INV + "/equipment-listings")
        require(len([r for r in rows if r["id"] in seen_ids]) == 20, "Phân phối listing theo owner không đúng")
    for url in media_urls:
        require(bool(url), "Thiếu URL ảnh")
        wait_for(lambda: public_image(url), "ảnh công khai")
    verify_boundaries(state, data, clients)
    report = {"kếtQuả": "Đạt", "catalog": catalog_counts, "tàiKhoản": 15, "tổChức": 5,
              "listing": len(seen_ids), "asset": len(seen_serials), "ảnhCôngKhai": len(media_urls),
              "kiểmTraQuyềnVàĐầuVào": "Đạt"}
    atomic_json(state.directory / "verification.json", report)
    return report
