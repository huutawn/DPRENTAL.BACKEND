"""Kiểm tra manifest, UTF-8, thông số có nguồn và file ảnh trước khi ghi API."""
import hashlib
import json
import math
import struct
import unicodedata
import urllib.parse


def need(condition, message):
    if not condition:
        raise ValueError(message)


def vietnamese(value, label):
    need(isinstance(value, str) and value == unicodedata.normalize("NFC", value), f"{label}: chuỗi phải là Unicode NFC")
    need(any(ord(c) > 127 for c in value), f"{label}: thiếu tiếng Việt có dấu")


def validate(data, root):
    need(len(data["categories"]) == 10 and len(data["brands"]) == 20 and len(data["types"]) == 50,
         "Bộ seed phải có 10 category, 20 brand, 50 type")
    categories = {c["key"] for c in data["categories"]}
    brands = {b["code"]: b for b in data["brands"]}
    need(len(categories) == 10 and len(brands) == 20, "Category/brand trùng khóa")
    need(len({t["key"] for t in data["types"]}) == 50, "Equipment type trùng khóa")
    used_brands, files = set(), set()
    for category in data["categories"]:
        vietnamese(category["name"], "Tên category")
        vietnamese(category["description"], "Mô tả category")
        files.add(category["image"])
    for brand in data["brands"]:
        files.add(brand["image"])
        need(bool(brand["logoSource"]), "Logo thương hiệu thiếu nguồn")
    for equipment in data["types"]:
        vietnamese(equipment["name"], "Tên type")
        vietnamese(equipment["description"], "Mô tả type")
        need(equipment["category"] in categories, "Type tham chiếu category không tồn tại")
        need(len(equipment["images"]) == 2 and len(set(equipment["images"])) == 2, "Type phải có hai góc ảnh")
        files.update(equipment["images"])
        fields = {f["key"]: f for f in equipment["schema"]}
        need(len(fields) == len(equipment["schema"]) and len(fields) >= 2, "Schema thiếu hoặc trùng thông số")
        need(any(f["primary"] for f in fields.values()), "Schema thiếu thông số chính")
        for field in fields.values():
            vietnamese(field["label"], "Nhãn thông số")
            need(field["dataType"] in ("number", "integer", "enum", "text", "boolean"), "Kiểu schema không hợp lệ")
            if field["dataType"] == "enum":
                need(bool(field.get("options")), "Enum thiếu lựa chọn")
                for option in field["options"]:
                    vietnamese(option["label"], "Nhãn lựa chọn")
        need(len(equipment["models"]) == 2, "Mỗi type phải có hai model")
        need(len({(m["brand"], m["model"]) for m in equipment["models"]}) == 2, "Hai listing phải dùng hai model khác nhau")
        for model in equipment["models"]:
            need(model["brand"] in brands, "Model tham chiếu brand không tồn tại")
            used_brands.add(model["brand"])
            need(model["brandName"] == brands[model["brand"]]["name"], "Tên thương hiệu không khớp")
            need(isinstance(model["dailyRate"], int) and 0 < model["dailyRate"] <= 100000000, "Giá thuê VND không hợp lệ")
            need(2000 <= model["manufactureYear"] <= 2026, "Năm sản xuất không hợp lệ")
            values = model["specValues"]
            need(set(values) <= set(fields), "Thông số nằm ngoài schema")
            need({f["key"] for f in fields.values() if f["required"]} <= set(values), "Thiếu thông số bắt buộc")
            facts = {f["key"]: f for f in model["sourceFacts"]}
            need(set(facts) == set(values), "Mỗi thông số phải có đúng một nguồn đối chiếu")
            for key, value in values.items():
                field, fact = fields[key], facts[key]
                need(fact["value"] == value and bool(fact["evidence"]), "Thông số khác bằng chứng nguồn")
                url = urllib.parse.urlsplit(fact["url"])
                domains = brands[model["brand"]]["sourceDomains"]
                need(url.scheme == "https" and url.hostname and any(
                    url.hostname == d or url.hostname.endswith("." + d) for d in domains), "Nguồn không thuộc hãng")
                if field["dataType"] in ("number", "integer"):
                    need(isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value), "Thông số phải là số hữu hạn")
                    need(field.get("min", value) <= value <= field.get("max", value), "Thông số vượt giới hạn schema")
                    need(field["dataType"] != "integer" or isinstance(value, int), "Thông số phải là số nguyên")
                    need(fact["unit"] == field["unit"], "Đơn vị thông số không khớp")
                elif field["dataType"] == "enum":
                    need(value in {o["value"] for o in field["options"]}, "Giá trị enum không hợp lệ")
                elif field["dataType"] == "boolean":
                    need(isinstance(value, bool), "Thông số phải là boolean")
                else:
                    need(isinstance(value, str), "Thông số phải là chuỗi")
            if equipment["key"] in {"diesel-excavator", "electric-excavator", "mini-loader", "skid-loader",
                                    "articulated-loader", "ride-roller", "electric-forklift"}:
                need(0 < values.get("operating_weight", 0) <= 3000, "Máy tự hành vượt giới hạn 3.000 kg hoặc thiếu khối lượng")
                need(0 < values.get("transport_width", 0) <= 1800, "Máy tự hành vượt chiều rộng 1.800 mm hoặc thiếu kích thước")
                if equipment["key"] == "electric-forklift":
                    need(0 < values.get("load_capacity", 0) <= 1500, "Xe nâng vượt tải nâng 1.500 kg")
    need(used_brands == set(brands), "Có thương hiệu không được dùng trong listing")
    files.update(("images/avatar.png", "images/organization.png", "images/business-license.png"))
    need(set(data["imageChecksums"]) == files, "Manifest checksum ảnh thiếu hoặc thừa file")
    for filename in files:
        path = (root / filename).resolve()
        need(path.is_relative_to(root.resolve()) and path.is_file(), f"Không tìm thấy ảnh đóng gói: {filename}")
        raw = path.read_bytes()
        logo = filename in {b["image"] for b in data["brands"]}
        need((100 if logo else 1000) <= len(raw) <= 8 * 1024 * 1024, f"Kích thước ảnh không hợp lệ: {filename}")
        need(raw.startswith(b"\x89PNG\r\n\x1a\n"), f"Ảnh phải là PNG: {filename}")
        width, height = struct.unpack(">II", raw[16:24])
        need(width >= (64 if logo else 256) and height >= (16 if logo else 256), f"Ảnh quá nhỏ: {filename}")
        need(hashlib.sha256(raw).hexdigest() == data["imageChecksums"][filename], f"Checksum ảnh sai: {filename}")
    return data


def load(root):
    path = root / "dataset.json"
    need(path.is_file(), "Thiếu dataset.json; bộ seed chưa sẵn sàng để chạy")
    raw = path.read_bytes()
    data = json.loads(raw.decode("utf-8"))
    validate(data, root)
    return data, hashlib.sha256(raw).hexdigest()
