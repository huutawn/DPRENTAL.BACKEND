"""Kế hoạch cố định và nhật ký ghi nguyên tử, khóa cho toàn bộ phiên seed và E2E scenarios."""
import datetime
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
        os.replace(temporary, path)
        try:
            os.chmod(path, 0o600)
        except OSError:
            pass
    finally:
        if os.path.exists(temporary):
            try:
                os.unlink(temporary)
            except OSError:
                pass

FIXED_PASSWORDS = {
    "admin-01": "0VlYoCNBn9w4MvvD2vQK539XnoyLQT0N!9aA",
    "admin-02": "9NH9E0uguExV8xnIHeI0IC-AYq2sIFVq!9aA",
    "admin-03": "axvi6Cv2Tb-5UTzBUkq01yZbgGQl1B0Z!9aA",
    "admin-04": "sfUjAoutcT97iCoXfhH92y2EX_6WEIVx!9aA",
    "admin-05": "L8B7_xJyh7QH2wid7cDRM_4OB8pax0lH!9aA",
    "user-01": "w6XulyS-as7km05ZvkmxinWbTCYyYJKR!9aA",
    "user-02": "zS0mhMwq4glL-5fxybcGfwN02ImlKI7y!9aA",
    "user-03": "tM2DH0KWe2_CyhH9Od0S27jEx-CHcAbT!9aA",
    "user-04": "had42onMfxBJXeEhC3CEC-Rir1ILhdpp!9aA",
    "user-05": "BXnbu4Uw72C0TTDrjJwHTgPF5Ew9fzS_!9aA",
    "owner-01": "RCMGWvDfDPghdKNCekIMi_vR1tJgdf-B!9aA",
    "owner-02": "qtcEBvL1xeGjPLb2Qz_cJ7LIeftxnB4G!9aA",
    "owner-03": "RCMGWvDfDPghdKNCekIMi_vR1tJgdf-B!9aA",
    "owner-04": "RCMGWvDfDPghdKNCekIMi_vR1tJgdf-B!9aA",
    "owner-05": "RCMGWvDfDPghdKNCekIMi_vR1tJgdf-B!9aA",
}

NAMES = {
    "admin": ["Nguyễn Minh Đức", "Trần Thu Hà", "Lê Hoàng Nam", "Phạm Ngọc Anh", "Võ Thanh Tâm"],
    "user": ["Nguyễn Văn Hùng", "Trần Thị Lan", "Lê Quang Vinh", "Phạm Bảo Ngọc", "Đặng Hải Sơn"],
    "owner": ["Nguyễn Thành Đạt", "Trần Minh Khôi", "Lê Thị Hạnh", "Phạm Quốc Bảo", "Võ Hoài An"],
}


def planned_quantities(count=100):
    generator = random.Random(20261013)
    return [generator.randint(10, 50) for _ in range(count)]


class State:
    def __init__(self, directory, base_url, fingerprint, project="dprental-dev", create=False):
        if project != "dprental-dev" and not project.startswith("dprental-e2e-"):
            raise ValueError(f"Project '{project}' không hợp lệ. Chỉ chấp nhận 'dprental-dev' hoặc 'dprental-e2e-*'.")
        self.directory = Path(directory)
        self.project = project
        self.base_url = base_url.rstrip("/")
        self.fingerprint = fingerprint
        if create:
            self.directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        if not self.directory.is_dir():
            raise ValueError("Không tìm thấy state; phải chạy --apply trước --verify")
        if self.directory.stat().st_mode & 0o077:
            raise ValueError("Thư mục state phải có quyền 0700")

        self.lock = (self.directory / ".lock").open("a")
        try:
            os.chmod(self.directory / ".lock", 0o600)
        except OSError:
            pass
        try:
            fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            self.lock.close()
            raise ValueError("Một tiến trình seed khác đang sử dụng state") from None
        try:
            self._load(self.base_url, fingerprint, create)
        except BaseException:
            self.lock.close()
            raise

    def _load(self, base_url, fingerprint, create):
        self.path = self.directory / "state.json"
        dataset_id = hashlib.sha256(f"{fingerprint}:{self.project}".encode()).hexdigest()
        if self.path.exists():
            if self.path.stat().st_mode & 0o077:
                raise ValueError("state.json phải có quyền 0600")
            self.data = json.loads(self.path.read_text(encoding="utf-8"))
            if self.data.get("baseUrl") != base_url or self.data.get("fingerprint") != fingerprint:
                raise ValueError("State khác URL đích hoặc bộ dữ liệu; không được trộn hai phiên seed")
            if self.data.get("datasetId") and self.data["datasetId"] != dataset_id:
                raise ValueError("State thuộc datasetId cũ hoặc project khác; cần dùng state mới sau khi reset")
        elif create:
            run_id = f"{self.project}-{datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')}-{secrets.token_hex(4)}"
            self.data = {
                "version": 2,
                "runId": run_id,
                "project": self.project,
                "baseUrl": base_url,
                "fingerprint": fingerprint,
                "datasetId": dataset_id,
                "createdAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "accounts": [],
                "listings": [],
                "operations": {},
                "uploads": {},
                "intents": {},
                "scenarios": {},
            }
            for group, names in NAMES.items():
                for index, name in enumerate(names):
                    number = len(self.data["accounts"]) + 1
                    key = f"{group}-{index+1:02}"
                    self.data["accounts"].append({
                        "key": key,
                        "kind": group,
                        "email": f"seed.{group}{index+1:02}@demo.dprental.test",
                        "fullName": name,
                        "password": FIXED_PASSWORDS.get(key, secrets.token_urlsafe(24) + "!9aA"),
                        "phoneNumber": f"090880{number:04}",
                        "citizenId": f"099090{number:06}",
                        "dateOfBirth": f"1990-05-{number:02}"
                    })
            for index, quantity in enumerate(planned_quantities()):
                self.data["listings"].append({
                    "key": f"ct-{index+1:03}",
                    "quantity": quantity,
                    "owner": f"owner-{index % 5 + 1:02}",
                    "serials": [
                        f"DEMO-{index+1:03}-{unit+1:02}-{secrets.token_hex(5).upper()}"
                        for unit in range(quantity)
                    ]
                })
            self.save()
        else:
            raise ValueError("Không tìm thấy state.json")

    def save(self):
        atomic_json(self.path, self.data)

    def close(self):
        try:
            self.lock.close()
        except OSError:
            pass

    def mark(self, key, status, record_id=None):
        self.data["operations"][key] = {"status": status, "id": record_id}
        self.save()

    def record_intent(self, key, payload_summary):
        self.data.setdefault("intents", {})[key] = {
            "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "summary": payload_summary,
            "status": "in_progress"
        }
        self.save()

    def clear_intent(self, key):
        if "intents" in self.data and key in self.data["intents"]:
            del self.data["intents"][key]
            self.save()

    def mark_scenario(self, scenario_id, status, next_eligible_at=None, detail=None, audit=None):
        if status not in ("passed", "waiting", "failed", "blocked"):
            raise ValueError(f"Trạng thái scenario không hợp lệ: {status}")
        self.data.setdefault("scenarios", {})[scenario_id] = {
            "status": status,
            "updatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "nextEligibleAt": next_eligible_at,
            "detail": detail or {},
            "audit": audit or []
        }
        self.save()

    def get_scenario(self, scenario_id):
        return self.data.get("scenarios", {}).get(scenario_id)

    def upload(self, api, path, key):
        checksum = hashlib.sha256(path.read_bytes()).hexdigest()
        cache_key = api.email + ":" + key + ":" + checksum
        if cache_key not in self.data["uploads"]:
            self.data["uploads"][cache_key] = api.upload(path, key)
            self.save()
        return self.data["uploads"][cache_key]
