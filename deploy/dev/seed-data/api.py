"""HTTP qua Gateway; không ghi token, mật khẩu hoặc payload vào log."""
import hashlib
import http.cookiejar
import json
import mimetypes
import socket
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class RequestBudget:
    def __init__(self, interval=0.5):
        self.interval = interval
        self.last_request = 0

    def wait(self):
        time.sleep(max(0, self.interval - (time.monotonic() - self.last_request)))
        self.last_request = time.monotonic()


class ApiError(RuntimeError):
    def __init__(self, method, path, status, payload=None):
        self.status = status
        self.payload = payload if isinstance(payload, dict) else {}
        title = self.payload.get("title", "Lỗi API")
        super().__init__(f"{method} {path.split('?')[0]}: HTTP {status}, {title}")


class UncertainWrite(RuntimeError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ApiError(req.method, req.full_url, code,
                       {"title": "API chuyển hướng; không chuyển tiếp thông tin xác thực"})


class Api:
    def __init__(self, base_url, email=None, password=None, interval=0.5, budget=None):
        url = urllib.parse.urlsplit(base_url)
        if url.scheme not in ("https", "http") or not url.netloc or url.username or url.query or url.fragment:
            raise ValueError("URL API không hợp lệ")
        if url.scheme == "http" and url.hostname not in ("localhost", "127.0.0.1", "::1"):
            raise ValueError("HTTP chỉ được dùng trên loopback để thử nghiệm")
        self.base_url = base_url.rstrip("/")
        self.email, self.password = email, password
        self.cookies = http.cookiejar.CookieJar()
        # Cookie chỉ dùng để nhận token đăng nhập, không tự gửi vào request ẩn danh.
        self.opener = urllib.request.build_opener(NoRedirect())
        self.token = None
        self.cookies.clear()
        self.logged_at = 0
        self.budget = budget or RequestBudget(interval)

    def login(self):
        if not self.email or not self.password:
            raise ValueError("Thiếu thông tin đăng nhập")
        self.token = None
        result = self.request("POST", "/api/v1/auth/login",
                              {"email": self.email, "password": self.password}, authenticated=False)
        self.token = next((c.value for c in self.cookies if c.name == "accessToken"), None)
        if not self.token:
            raise RuntimeError("Đăng nhập thành công nhưng thiếu cookie accessToken")
        self.logged_at = time.monotonic()
        return result

    def request(self, method, path, body=None, headers=None, authenticated=True):
        if not path.startswith("/api/v1/") or ".." in path:
            raise ValueError("Đường dẫn phải nằm trong /api/v1/")
        if authenticated and self.email and (not self.token or time.monotonic() - self.logged_at > 720):
            self.login()
        request_headers = {"User-Agent": "dprental-seed/2.0", "Accept": "application/json"}
        request_headers.update(headers or {})
        if authenticated and self.token:
            request_headers["Authorization"] = f"Bearer {self.token}"
        if isinstance(body, (dict, list)):
            body = json.dumps(body, ensure_ascii=False).encode("utf-8")
            request_headers["Content-Type"] = "application/json"
        refreshed = False
        for attempt in range(7):
            self.budget.wait()
            request = urllib.request.Request(self.base_url + path, body, request_headers, method=method)
            try:
                with self.opener.open(request, timeout=60) as response:
                    if path == "/api/v1/auth/login":
                        self.cookies.extract_cookies(response, request)
                    data = response.read(16 * 1024 * 1024 + 1)
                    if len(data) > 16 * 1024 * 1024:
                        raise RuntimeError("Phản hồi API vượt giới hạn 16 MiB")
                    return json.loads(data) if data else None
            except urllib.error.HTTPError as error:
                payload = error.read(65536)
                try:
                    payload = json.loads(payload)
                except (ValueError, UnicodeError):
                    payload = None
                if error.code == 429 and attempt < 6:
                    retry = error.headers.get("Retry-After", "10")
                    wait = min(60, max(10, int(retry))) if retry.isdecimal() else 10
                    time.sleep(wait)
                    continue
                if error.code == 401 and authenticated and self.email and not refreshed:
                    self.login()
                    refreshed = True
                    request_headers["Authorization"] = f"Bearer {self.token}"
                    continue
                raise ApiError(method, path, error.code, payload) from None
            except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError) as error:
                if method == "GET" and attempt < 2:
                    time.sleep(2 ** attempt)
                    continue
                if method != "GET":
                    raise UncertainWrite(f"Chưa rõ kết quả {method} {path}; cần đối soát trước khi ghi lại") from None
                raise RuntimeError(f"Không kết nối được API {path}") from error
        raise RuntimeError("Đã hết số lần thử API")

    def pages(self, path, **filters):
        for page in range(1, 1001):
            query = urllib.parse.urlencode({"page": page, "pageSize": 100, **filters})
            response = self.request("GET", path + "?" + query)
            if isinstance(response, list):
                return response
            items = response.get("items", [])
            if page == 1:
                result = []
            result.extend(items)
            total = response.get("totalCount", response.get("total"))
            if not items or total is not None and len(result) >= total or len(items) < 100:
                return result
        raise RuntimeError(f"Phân trang vượt giới hạn: {path}")

    def upload(self, path, logical_key):
        data = path.read_bytes()
        checksum = hashlib.sha256(data).hexdigest()
        boundary = uuid.uuid4().hex
        mime = mimetypes.guess_type(path.name)[0]
        if mime not in ("image/png", "image/jpeg", "image/webp", "application/pdf"):
            raise ValueError(f"Định dạng file không được hỗ trợ: {path.name}")
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="Files"; filename="{path.name}"\r\n'
                f'Content-Type: {mime}\r\n\r\n').encode() + data
        body += (f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="Sha256Checksums"\r\n\r\n'
                 f'{checksum}\r\n--{boundary}--\r\n').encode()
        key = str(uuid.uuid5(uuid.NAMESPACE_URL, self.base_url + ":" + self.email + ":" + logical_key + ":" + checksum))
        result = self.request("POST", "/api/v1/media/uploads", body, {
            "Content-Type": f"multipart/form-data; boundary={boundary}", "Idempotency-Key": key})
        if not result or result[0].get("error") or not result[0].get("media"):
            raise RuntimeError(f"Upload thất bại: {path.name}")
        return result[0]["media"]["mediaId"]


def public_image(url):
    """Không gửi cookie/token khi kiểm tra ảnh, kể cả URL ký của MinIO."""
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme not in ("https", "http") or parsed.username:
        raise ValueError("URL ảnh không hợp lệ")
    request = urllib.request.Request(url, headers={"User-Agent": "dprental-seed/2.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        mime = response.headers.get_content_type()
        signature = response.read(16)
        if not mime.startswith("image/") or not signature:
            raise RuntimeError("Ảnh công khai không trả nội dung ảnh")
