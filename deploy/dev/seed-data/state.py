"""Kế hoạch cố định và nhật ký ghi nguyên tử, khóa cho toàn bộ phiên seed."""
import fcntl
import hashlib
import json
import os
import random
import secrets
import tempfile
from pathlib import Path


def atomic_json(path, value):
    descriptor, temporary = tempfile.mkstemp(prefix=".seed-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(value, output, ensure_ascii=False, indent=2)
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
        parent = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(parent)
        finally:
            os.close(parent)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


NAMES = {
    "admin": ["Nguyễn Minh Đức", "Trần Thu Hà", "Lê Hoàng Nam", "Phạm Ngọc Anh", "Võ Thanh Tâm"],
    "user": ["Nguyễn Văn Hùng", "Trần Thị Lan", "Lê Quang Vinh", "Phạm Bảo Ngọc", "Đặng Hải Sơn"],
    "owner": ["Nguyễn Thành Đạt", "Trần Minh Khôi", "Lê Thị Hạnh", "Phạm Quốc Bảo", "Võ Hoài An"],
}


def planned_quantities(count=100):
    generator = random.Random(20261013)
    return [generator.randint(10, 50) for _ in range(count)]


class State:
    def __init__(self, directory, base_url, fingerprint, create=False):
        self.directory = Path(directory)
        if create:
            self.directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        if not self.directory.is_dir():
            raise ValueError("Không tìm thấy state; phải chạy --apply trước --verify")
        if self.directory.stat().st_mode & 0o077:
            raise ValueError("Thư mục state phải có quyền 0700")
        self.lock = (self.directory / ".lock").open("a")
        os.chmod(self.directory / ".lock", 0o600)
        try:
            fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            self.lock.close()
            raise ValueError("Một tiến trình seed khác đang sử dụng state") from None
        try:
            self._load(base_url, fingerprint, create)
        except BaseException:
            self.lock.close()
            raise

    def _load(self, base_url, fingerprint, create):
        self.path = self.directory / "state.json"
        if self.path.exists():
            if self.path.stat().st_mode & 0o077:
                raise ValueError("state.json phải có quyền 0600")
            self.data = json.loads(self.path.read_text(encoding="utf-8"))
            if self.data["baseUrl"] != base_url or self.data["fingerprint"] != fingerprint:
                raise ValueError("State khác URL đích hoặc bộ dữ liệu; không được trộn hai phiên seed")
        elif create:
            self.data = {"baseUrl": base_url, "fingerprint": fingerprint, "accounts": [],
                         "listings": [], "operations": {}, "uploads": {}}
            for group, names in NAMES.items():
                for index, name in enumerate(names):
                    number = len(self.data["accounts"]) + 1
                    self.data["accounts"].append({"key": f"{group}-{index+1:02}", "kind": group,
                        "email": f"seed.{group}{index+1:02}@demo.dprental.test", "fullName": name,
                        "password": secrets.token_urlsafe(24) + "!9aA", "phoneNumber": f"090880{number:04}",
                        "citizenId": f"099090{number:06}", "dateOfBirth": f"1990-05-{number:02}"})
            for index, quantity in enumerate(planned_quantities()):
                self.data["listings"].append({"key": f"ct-{index+1:03}", "quantity": quantity,
                    "owner": f"owner-{index % 5 + 1:02}", "serials": [
                        f"DEMO-{index+1:03}-{unit+1:02}-{secrets.token_hex(5).upper()}" for unit in range(quantity)]})
            self.save()
        else:
            raise ValueError("Không tìm thấy state.json")

    def save(self):
        atomic_json(self.path, self.data)

    def close(self):
        self.lock.close()

    def mark(self, key, status, record_id=None):
        self.data["operations"][key] = {"status": status, "id": record_id}
        self.save()

    def upload(self, api, path, key):
        # Ready/upload ownership is checked again by the consuming business API.
        checksum = hashlib.sha256(path.read_bytes()).hexdigest()
        cache_key = api.email + ":" + key + ":" + checksum
        if cache_key not in self.data["uploads"]:
            self.data["uploads"][cache_key] = api.upload(path, key)
            self.save()
        return self.data["uploads"][cache_key]
