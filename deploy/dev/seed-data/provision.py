"""Tạo tài khoản, tổ chức và Catalog qua API hiện hữu."""
import time
from api import Api, ApiError, RequestBudget, UncertainWrite

AUTH = "/api/v1/auth"
CAT = "/api/v1/catalog"
INV = "/api/v1/inventory"
MARKER = "Dữ liệu demo thiết bị công trường DPRENTAL; ảnh minh họa theo loại."
# Auth/Catalog DTO enum được serialize thành số; schema và Inventory status là chuỗi.
ORG_SUPPLIER, ORG_PENDING, ORG_ACTIVE = 1, 0, 1
TYPE_DRAFT, TYPE_PUBLISHED = 0, 1


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def reconcile(lookup):
    """Chờ read model cập nhật, chỉ GET và không phát sinh lần ghi mới."""
    for attempt in range(6):
        record = lookup()
        if record:
            return record
        if attempt < 5:
            time.sleep(5)
    return None


def create(state, key, api, path, payload, lookup, validate, id_field="id", headers=None, accept_response=False):
    """Không gửi lại POST chưa rõ kết quả; đối soát bằng khóa nghiệp vụ."""
    record = lookup()
    operation = state.data["operations"].get(key)
    if record:
        validate(record)
        if operation and operation.get("id") and operation["id"] != record[id_field]:
            raise RuntimeError(f"ID đã thay đổi ở {key}; có thể database đã reset. Dùng state mới sau khi kiểm tra.")
        state.mark(key, "done", record[id_field])
        return record
    if operation and operation["status"] != "rejected":
        raise RuntimeError(f"{key}: không tìm thấy bản ghi của lần ghi trước ({operation['status']}); cần đối soát thủ công")
    state.mark(key, "pending")
    try:
        response = api.request("POST", path, payload, headers)
    except (UncertainWrite, ApiError) as error:
        if isinstance(error, ApiError) and error.status not in (409, 500, 502, 503, 504):
            state.mark(key, "rejected")
            raise
        record = reconcile(lookup)
        if record is None:
            raise RuntimeError(f"{key}: chưa xác định được kết quả; giữ state pending, không tự gửi lại POST") from error
    else:
        record = response if accept_response and isinstance(response, dict) else reconcile(lookup)
    require(record is not None, f"{key}: API báo thành công nhưng chưa đọc lại được bản ghi")
    validate(record)
    state.mark(key, "done", record[id_field])
    return record


def match(records, field, value):
    found = [r for r in records if r.get(field) == value]
    require(len(found) <= 1, f"Có nhiều bản ghi cùng {field}={value}")
    return found[0] if found else None


def equal(record, expected, label):
    for field, value in expected.items():
        require(record.get(field) == value, f"{label}: trường {field} không khớp bộ seed")


def accounts(state, root, bootstrap_key, budget=None):
    clients = {}
    budget = budget or RequestBudget()
    admin = None
    admin_role = None
    for account in state.data["accounts"]:
        client = Api(state.data["baseUrl"], account["email"], account["password"], budget=budget)
        try:
            client.login()
        except ApiError as error:
            if error.status != 401:
                raise
            key = "account:" + account["key"]
            require(key not in state.data["operations"] or state.data["operations"][key]["status"] == "rejected",
                    f"Không đăng nhập được {account['key']} đã ghi trong state; dừng để kiểm tra")
            if admin is None:
                require(bool(bootstrap_key), "Thiếu AUTH_ADMIN_BOOTSTRAP_CREATION_KEY cho admin đầu tiên")
            state.mark(key, "pending")
            payload = {field: account[field] for field in ("email", "password", "fullName")}
            try:
                if admin is None:
                    require(bool(bootstrap_key), "Thiếu AUTH_ADMIN_BOOTSTRAP_CREATION_KEY cho admin đầu tiên")
                    client.request("POST", AUTH + "/admins", payload,
                                   {"X-Admin-Creation-Key": bootstrap_key}, authenticated=False)
                elif account["kind"] == "admin":
                    payload["roleIds"] = [admin_role]
                    admin.request("POST", AUTH + "/admin/users", payload)
                else:
                    payload.update({"phoneNumber": account["phoneNumber"], "dateOfBirth": account["dateOfBirth"]})
                    client.request("POST", AUTH + "/register", payload, authenticated=False)
            except (UncertainWrite, ApiError) as write_error:
                if isinstance(write_error, ApiError) and write_error.status not in (409, 500, 502, 503, 504):
                    state.mark(key, "rejected")
                    raise
                # Đăng nhập chứng minh tài khoản đã tạo bằng đúng mật khẩu; không lặp POST.
            client.login()
        profile = client.request("GET", AUTH + "/users/me")
        equal(profile, {"email": account["email"], "fullName": account["fullName"]}, account["key"])
        if account["kind"] == "admin":
            require("admin" in profile["roles"], "Tài khoản admin thiếu role admin")
        account["id"] = profile["id"]
        state.mark("account:" + account["key"], "done", profile["id"])
        avatar = state.upload(client, root / "images/avatar.png", "avatar")
        if not profile["isProfileCompleted"]:
            payload = {field: account[field] for field in ("fullName", "phoneNumber", "citizenId", "dateOfBirth")}
            payload["avatarMediaId"] = avatar
            try:
                client.request("POST", AUTH + "/users/profile", payload)
            except UncertainWrite:
                require(client.request("GET", AUTH + "/users/me")["isProfileCompleted"],
                        "Chưa rõ kết quả hoàn thiện hồ sơ")
        elif profile.get("avatarMediaId") != avatar:
            require(False, "Ảnh hồ sơ đã bị thay đổi; không tự ghi đè")
        clients[account["key"]] = client
        if admin is None:
            admin = client
            roles = admin.request("GET", AUTH + "/admin/users/roles")
            role = match(roles, "name", "admin")
            require(role is not None, "API không cung cấp role admin")
            admin_role = role["id"]
        print(f"Tài khoản {account['key']}: hồ sơ và quyền hợp lệ")
    return clients


ORGS = [
    ("Công ty Thiết bị Công trường An Phát", "Thành phố Hồ Chí Minh", "Phường Thủ Đức", "18 Đường Số 8"),
    ("Công ty Cho thuê Máy Xây dựng Minh Thành", "Thành phố Hà Nội", "Phường Cầu Giấy", "26 Đường Dịch Vọng"),
    ("Công ty Thiết bị Xây dựng Hòa Bình", "Thành phố Đà Nẵng", "Phường Hải Châu", "35 Đường Núi Thành"),
    ("Công ty Dịch vụ Công trường Đại Việt", "Thành phố Hải Phòng", "Phường Lê Chân", "42 Đường Trần Nguyên Hãn"),
    ("Công ty Máy và Thiết bị Nam Việt", "Thành phố Cần Thơ", "Phường Ninh Kiều", "56 Đường Nguyễn Trãi"),
]


def organizations(state, root, clients):
    admin = clients["admin-01"]
    for index, (name, province, ward, address) in enumerate(ORGS, start=1):
        key = f"owner-{index:02}"
        owner = clients[key]
        account = next(a for a in state.data["accounts"] if a["key"] == key)
        logo = state.upload(owner, root / "images/organization.png", "organization-logo")
        document = state.upload(owner, root / "images/business-license.png", "business-license")
        slug = f"demo-cong-truong-{index:02}"
        payload = {"name": name, "type": ORG_SUPPLIER, "slug": slug, "taxCode": f"09999000{index:02}",
            "legalRepresentativeName": account["fullName"], "legalRepresentativeCitizenId": account["citizenId"],
            "contactEmail": account["email"], "contactPhone": account["phoneNumber"],
            "address": address, "province": province, "ward": ward, "logoMediaId": logo,
            "businessLicenseDocumentMediaId": document, "businessLicenseNumber": f"DEMO-CT-{index:02}",
            "businessLicenseIssuedDate": "2024-01-15", "businessLicenseIssuedPlace": "Hồ sơ mô phỏng cho môi trường thử nghiệm",
            "establishedDate": "2020-01-15", "description": MARKER}
        def lookup():
            return owner.request("GET", AUTH + "/organizations/mine")
        def validate(record):
            equal(record, {"slug": slug, "name": name, "taxCode": payload["taxCode"],
                           "logoMediaId": logo, "businessLicenseDocumentMediaId": document}, slug)
        record = create(state, "organization:" + key, owner, AUTH + "/organizations/register", payload,
                        lookup, validate)
        if record["status"] == ORG_PENDING:
            try:
                admin.request("PATCH", AUTH + f"/admin/organizations/{record['id']}/approve")
            except UncertainWrite:
                require(lookup()["status"] == ORG_ACTIVE, "Chưa rõ kết quả duyệt tổ chức")
        require(lookup()["status"] == ORG_ACTIVE, f"Tổ chức {slug} chưa Active")
        owner.login()
        profile = owner.request("GET", AUTH + "/users/me")
        require("inventory.asset.manage" in profile["permissions"], "Owner chưa có quyền quản lý kho")
        print(f"Tổ chức {name}: Active")


def catalog(state, root, data, admin):
    category_ids, brand_ids, type_ids = {}, {}, {}
    for category in data["categories"]:
        media_id = state.upload(admin, root / category["image"], "category:" + category["key"])
        payload = {"name": category["name"], "description": category["description"], "imageMediaId": media_id}
        def lookup():
            row = match(admin.pages(CAT + "/admin/categories"), "name", category["name"])
            return admin.request("GET", CAT + f"/admin/categories/{row['id']}") if row else None
        record = create(state, "category:" + category["key"], admin, CAT + "/categories", payload,
                        lookup, lambda r: equal(r, payload, category["key"]))
        require(record["isActive"], "Category không hoạt động")
        category_ids[category["key"]] = record["id"]
    for brand in data["brands"]:
        media_id = state.upload(admin, root / brand["image"], "brand:" + brand["code"])
        payload = {"code": brand["code"], "name": brand["name"], "slug": brand["slug"], "logoMediaId": media_id}
        def lookup():
            row = match(admin.pages(CAT + "/admin/brands"), "code", brand["code"])
            return admin.request("GET", CAT + f"/admin/brands/{row['id']}") if row else None
        record = create(state, "brand:" + brand["code"], admin, CAT + "/brands", payload,
                        lookup, lambda r: equal(r, payload, brand["code"]))
        require(record["isActive"], "Brand không hoạt động")
        brand_ids[brand["code"]] = record["id"]
    for equipment in data["types"]:
        code = "CT_" + equipment["key"].replace("-", "_").upper()
        media = [{"mediaId": state.upload(admin, root / image, "type:" + equipment["key"] + f":{i}"),
                  "displayOrder": i, "isThumbnail": i == 0} for i, image in enumerate(equipment["images"])]
        payload = {"code": code, "name": equipment["name"], "description": equipment["description"],
                   "categoryIds": [category_ids[equipment["category"]]], "media": media}
        def lookup():
            row = match(admin.pages(CAT + "/admin/equipment-types"), "code", code)
            return admin.request("GET", CAT + f"/admin/equipment-types/{row['id']}") if row else None
        def validate(record):
            equal(record, {k: payload[k] for k in ("code", "name", "description")}, code)
            require([c["id"] for c in record["categories"]] == payload["categoryIds"], "Category của type không khớp")
            require([m["mediaId"] for m in record["media"]] == [m["mediaId"] for m in media], "Ảnh type không khớp")
        record = create(state, "type:" + code, admin, CAT + "/equipment-types", payload, lookup, validate)
        path = CAT + f"/equipment-types/{record['id']}"
        definition = {"fields": equipment["schema"]}
        def schema_lookup():
            rows = admin.request("GET", path + "/spec-schemas")
            require(len(rows) <= 1, "Type seed có nhiều phiên bản schema; cần kiểm tra thủ công")
            return rows[0] if rows else None
        schema = create(state, "schema:" + code, admin, path + "/spec-schemas", definition,
                        schema_lookup, lambda r: equal(r, {"definition": definition}, code))
        if schema["status"] == "Draft":
            admin.request("POST", path + f"/spec-schemas/{schema['version']}/publish")
        require(schema_lookup()["status"] == "Published", "Schema chưa publish")
        record = lookup()
        if record["status"] == TYPE_DRAFT:
            admin.request("POST", path + "/publish")
        require(lookup()["status"] == TYPE_PUBLISHED, "Type chưa publish")
        type_ids[equipment["key"]] = (record["id"], schema["version"])
        print(f"Catalog: {equipment['name']}")
    return category_ids, brand_ids, type_ids
