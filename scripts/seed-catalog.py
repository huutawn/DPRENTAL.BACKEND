#!/usr/bin/env python3
"""Seeds sample brands, categories and equipment types into CatalogService.

Each record gets one image uploaded to MediaService first; the returned mediaId
is then referenced when the record is created. Safe to re-run: records that
already exist (same code/name, or a 409 from the API) are skipped.

    python scripts/seed-catalog.py              # asks for admin email/password
    DPRENTAL_TOKEN=<jwt> python scripts/seed-catalog.py
"""
import argparse
import getpass
import hashlib
import http.cookiejar
import json
import mimetypes
import os
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zlib
from pathlib import Path

# (code, name)
BRANDS = [
    ("SONY", "Sony"),
    ("CANON", "Canon"),
    ("NIKON", "Nikon"),
    ("FUJIFILM", "Fujifilm"),
    ("PANASONIC", "Panasonic"),
    ("BLACKMAGIC", "Blackmagic Design"),
    ("DJI", "DJI"),
    ("ARRI", "ARRI"),
    ("RED", "RED Digital Cinema"),
    ("APUTURE", "Aputure"),
]

# (name, description)
CATEGORIES = [
    ("Máy ảnh", "Máy ảnh mirrorless, DSLR và compact."),
    ("Máy quay phim", "Máy quay cinema và camcorder chuyên dụng."),
    ("Ống kính", "Ống kính zoom, prime và cine."),
    ("Đèn chiếu sáng", "Đèn LED, đèn flash và phụ kiện tản sáng."),
    ("Thiết bị âm thanh", "Micro, máy ghi âm và bộ thu phát không dây."),
    ("Gimbal và chống rung", "Gimbal cầm tay và hệ thống ổn định."),
    ("Flycam", "Drone quay phim, chụp ảnh trên không."),
    ("Chân máy", "Tripod, monopod và slider."),
    ("Màn hình kiểm tra", "Monitor gắn máy và monitor đạo diễn."),
    ("Phụ kiện", "Pin, thẻ nhớ, cage và phụ kiện khác."),
]

# Target status -> (code, name, description, category names).
# Every equipment type is created as Draft; "published" is then published and
# "disabled" is published and disabled again (hidden from the public catalog).
EQUIPMENT_TYPES = {
    "published": [
        ("MIRRORLESS", "Máy ảnh mirrorless", "Thân máy ảnh không gương lật.", ["Máy ảnh"]),
        ("DSLR", "Máy ảnh DSLR", "Thân máy ảnh gương lật.", ["Máy ảnh"]),
        ("CINECAM", "Máy quay cinema", "Máy quay phim điện ảnh cảm biến lớn.", ["Máy quay phim"]),
        ("ZOOMLENS", "Ống kính zoom", "Ống kính thay đổi tiêu cự.", ["Ống kính"]),
        ("PRIMELENS", "Ống kính prime", "Ống kính một tiêu cự, khẩu độ lớn.", ["Ống kính"]),
        ("LEDLIGHT", "Đèn LED quay phim", "Đèn LED chiếu sáng liên tục.", ["Đèn chiếu sáng"]),
        ("WIRELESSMIC", "Micro không dây", "Bộ micro thu phát không dây.", ["Thiết bị âm thanh"]),
        ("GIMBAL", "Gimbal cầm tay", "Gimbal chống rung 3 trục.", ["Gimbal và chống rung"]),
        ("DRONE", "Flycam quay phim", "Drone quay phim trên không.", ["Flycam"]),
        ("TRIPOD", "Chân máy quay", "Tripod đầu dầu cho máy quay.", ["Chân máy"]),
    ],
    "draft": [
        ("COMPACTCAM", "Máy ảnh compact", "Máy ảnh nhỏ gọn ống kính liền.", ["Máy ảnh"]),
        ("ACTIONCAM", "Camera hành động", "Camera nhỏ gọn chống rung, chống nước.", ["Máy quay phim"]),
        ("CAMCORDER", "Máy quay camcorder", "Máy quay cầm tay cho sự kiện.", ["Máy quay phim"]),
        ("CINELENS", "Ống kính cine", "Ống kính điện ảnh có vòng gear.", ["Ống kính"]),
        ("FLASH", "Đèn flash", "Đèn flash rời gắn hotshoe.", ["Đèn chiếu sáng"]),
        ("SOFTBOX", "Softbox", "Hộp tản sáng cho đèn studio.", ["Đèn chiếu sáng"]),
        ("SHOTGUNMIC", "Micro shotgun", "Micro định hướng gắn máy hoặc boom.", ["Thiết bị âm thanh"]),
        ("RECORDER", "Máy ghi âm", "Máy ghi âm hiện trường đa kênh.", ["Thiết bị âm thanh"]),
        ("MONOPOD", "Monopod", "Chân máy một chân.", ["Chân máy"]),
        ("SLIDER", "Slider", "Ray trượt cho máy quay.", ["Chân máy"]),
    ],
    "disabled": [
        ("CAM360", "Camera 360", "Camera quay toàn cảnh 360 độ.", ["Máy quay phim"]),
        ("FILMCAM", "Máy ảnh film", "Máy ảnh dùng phim 35mm.", ["Máy ảnh"]),
        ("MACROLENS", "Ống kính macro", "Ống kính chụp cận cảnh.", ["Ống kính"]),
        ("TELELENS", "Ống kính tele", "Ống kính tiêu cự dài.", ["Ống kính"]),
        ("RINGLIGHT", "Đèn ring", "Đèn LED dạng vòng.", ["Đèn chiếu sáng"]),
        ("LAVMIC", "Micro cài áo", "Micro lavalier có dây.", ["Thiết bị âm thanh"]),
        ("FIELDMONITOR", "Monitor gắn máy", "Màn hình kiểm tra gắn trên máy quay.", ["Màn hình kiểm tra"]),
        ("VIDEOTX", "Bộ truyền hình ảnh không dây", "Bộ phát và thu tín hiệu video.", ["Màn hình kiểm tra"]),
        ("VMOUNT", "Pin V-mount", "Pin dung lượng lớn cho máy quay và đèn.", ["Phụ kiện"]),
        ("CAMCAGE", "Cage máy quay", "Khung bảo vệ và gắn phụ kiện.", ["Phụ kiện"]),
    ],
}

# An equipment type can only be published once it has a published spec schema.
SPEC_FIELDS = [
    {"key": "weight", "label": "Trọng lượng", "dataType": "number", "required": False,
     "filterable": False, "primary": False, "displayOrder": 0, "min": 0, "unit": "kg"},
    {"key": "condition", "label": "Tình trạng", "dataType": "enum", "required": False,
     "filterable": True, "primary": False, "displayOrder": 1,
     "options": [{"value": "new", "label": "Mới"}, {"value": "used", "label": "Đã qua sử dụng"}]},
]

IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp"}
# The gateway's Cloudflare rules reject urllib's default User-Agent (error 1010).
USER_AGENT = "dprental-seed-catalog/1.0"
RATE_LIMIT_RETRIES = 12
RATE_LIMIT_WAIT_SECONDS = 10


class ApiError(Exception):
    def __init__(self, method, path, status, body):
        super().__init__(f"{method} {path} -> {status}\n{body}")
        self.status = status


class Api:
    def __init__(self, base_url):
        self.base_url = base_url.rstrip("/")
        self.token = None

    def login(self, email, password):
        # Login only sets cookies; the accessToken cookie is the JWT used as Bearer.
        cookies = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))
        request = urllib.request.Request(
            f"{self.base_url}/api/v1/auth/login",
            data=json.dumps({"email": email, "password": password}).encode(),
            headers={"Content-Type": "application/json", "User-Agent": USER_AGENT},
            method="POST",
        )
        try:
            opener.open(request, timeout=30).close()
        except urllib.error.HTTPError as error:
            raise SystemExit(f"Login failed ({error.code}): {error.read().decode(errors='replace')}")
        self.token = next((c.value for c in cookies if c.name == "accessToken"), None)
        if not self.token:
            raise SystemExit("Login succeeded but no accessToken cookie was returned")

    def request(self, method, path, body=None, headers=None):
        headers = dict(headers or {})
        headers["Authorization"] = f"Bearer {self.token}"
        headers["User-Agent"] = USER_AGENT
        if isinstance(body, dict):
            body = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self.base_url + path, data=body, headers=headers, method=method)
        for attempt in range(RATE_LIMIT_RETRIES + 1):
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    payload = response.read()
                return json.loads(payload) if payload else None
            except urllib.error.HTTPError as error:
                # The gateway rejects a rate-limited request before it reaches a service, so retrying is safe.
                if error.code != 429 or attempt == RATE_LIMIT_RETRIES:
                    raise ApiError(method, path, error.code, error.read().decode(errors="replace")) from None
                # Retry-After is always the 1s minimum, far shorter than the sliding window.
                time.sleep(RATE_LIMIT_WAIT_SECONDS)


def placeholder_png(seed, size=256):
    """Solid-colour PNG; the colour is derived from the seed so every file differs."""
    colour = hashlib.sha256(seed.encode()).digest()[:3]
    raw = (b"\x00" + colour * size) * size

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


class Images:
    """Hands out one image per record: files from --images in turn, else placeholders."""

    def __init__(self, directory):
        self.files = []
        if directory:
            self.files = sorted(p for p in Path(directory).iterdir() if p.suffix.lower() in IMAGE_EXTENSIONS)
            if not self.files:
                raise SystemExit(f"No {sorted(IMAGE_EXTENSIONS)} files in {directory}")
        self.used = 0

    def next(self, label):
        if not self.files:
            return f"{label}.png", "image/png", placeholder_png(label)
        path = self.files[self.used % len(self.files)]
        self.used += 1
        return path.name, mimetypes.guess_type(path.name)[0] or "application/octet-stream", path.read_bytes()


def upload(api, images, label):
    """Uploads the next image and returns its mediaId, or None with --no-images."""
    if images is None:
        return None
    filename, content_type, data = images.next(label)
    boundary = uuid.uuid4().hex
    body = b"".join([
        f'--{boundary}\r\nContent-Disposition: form-data; name="Files"; filename="{filename}"\r\n'
        f"Content-Type: {content_type}\r\n\r\n".encode(),
        data,
        f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="Sha256Checksums"\r\n\r\n'
        f"{hashlib.sha256(data).hexdigest()}\r\n--{boundary}--\r\n".encode(),
    ])
    results = api.request("POST", "/api/v1/media/uploads", body, {
        "Content-Type": f"multipart/form-data; boundary={boundary}",
        "Idempotency-Key": str(uuid.uuid4()),
    })
    result = results[0] if results else {}
    if result.get("error") or not result.get("media"):
        raise SystemExit(f"Upload of {filename} failed: {json.dumps(result, ensure_ascii=False)}")
    return result["media"]["mediaId"]


def create(api, path, payload, label):
    """Returns the created record, or None when the API reports it already exists."""
    try:
        record = api.request("POST", path, payload)
    except ApiError as error:
        if error.status != 409:
            raise
        print(f"  = {label} (already exists)")
        return None
    print(f"  + {label} -> {record['id']}")
    return record


def seed_brands(api, images, count):
    print("Brands")
    for code, name in BRANDS[:count]:
        found = api.request("GET", f"/api/v1/catalog/admin/brands?Keyword={urllib.parse.quote(name)}&PageSize=20")
        if any(item["code"] == code for item in found["items"]):
            print(f"  = {name} (already exists)")
            continue
        create(api, "/api/v1/catalog/brands", {
            "code": code,
            "name": name,
            "slug": code.lower(),
            "logoMediaId": upload(api, images, f"brand-{code.lower()}"),
        }, name)


def seed_categories(api, images, count):
    print("Categories")
    ids = {item["name"]: item["id"] for item in api.request("GET", "/api/v1/catalog/categories")}
    for index, (name, description) in enumerate(CATEGORIES[:count], start=1):
        if name in ids:
            print(f"  = {name} (already exists)")
            continue
        record = create(api, "/api/v1/catalog/categories", {
            "name": name,
            "description": description,
            "imageMediaId": upload(api, images, f"category-{index:02d}"),
        }, name)
        if record:
            ids[name] = record["id"]
    return ids


def apply_status(api, equipment_type_id, status):
    """Moves a freshly created (Draft, no schema) equipment type to its target status."""
    if status == "draft":
        return
    path = f"/api/v1/catalog/equipment-types/{equipment_type_id}"
    schema = api.request("POST", f"{path}/spec-schemas", {"fields": SPEC_FIELDS})
    api.request("POST", f"{path}/spec-schemas/{schema['version']}/publish")
    api.request("POST", f"{path}/publish")
    if status == "disabled":
        api.request("POST", f"{path}/disable")


def seed_equipment_types(api, images, count, category_ids):
    for status, entries in EQUIPMENT_TYPES.items():
        print(f"Equipment types ({status})")
        for code, name, description, categories in entries[:count]:
            missing = [category for category in categories if category not in category_ids]
            if missing:
                print(f"  ! {name} skipped, category not found: {', '.join(missing)}")
                continue
            media_id = upload(api, images, f"equipment-type-{code.lower()}")
            record = create(api, "/api/v1/catalog/equipment-types", {
                "code": code,
                "name": name,
                "description": description,
                "categoryIds": [category_ids[category] for category in categories],
                "media": [{"mediaId": media_id, "displayOrder": 0, "isThumbnail": True}] if media_id else [],
            }, name)
            if record:
                apply_status(api, record["id"], status)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base-url", default="https://dev-api.lazyops.cloud")
    parser.add_argument("--count", type=int, default=10,
                        help="brands, categories and equipment types per status to seed (max 10)")
    parser.add_argument("--images", help="folder of png/jpg/webp files to upload instead of placeholders")
    parser.add_argument("--no-images", action="store_true", help="create records without uploading images")
    parser.add_argument("--email", help="admin email; asked for when omitted and DPRENTAL_TOKEN is unset")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    api = Api(args.base_url)
    images = None if args.no_images else Images(args.images)
    api.token = os.environ.get("DPRENTAL_TOKEN", "").strip() or None
    if not api.token:
        api.login(args.email or input("Admin email: ").strip(), getpass.getpass("Password: "))

    try:
        seed_brands(api, images, args.count)
        category_ids = seed_categories(api, images, args.count)
        seed_equipment_types(api, images, args.count, category_ids)
    except ApiError as error:
        hint = " (token expired or not an admin?)" if error.status in (401, 403) else ""
        raise SystemExit(f"Stopped{hint}: {error}")
    print("Done")


if __name__ == "__main__":
    main()
