import copy
import json
import socket
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from api import Api, ApiError, UncertainWrite
from fixture import load, validate
from provision import create, equal
from state import State, planned_quantities
from inventory import rental_rate


class StateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.path = Path(self.temporary.name) / "state"

    def tearDown(self):
        self.temporary.cleanup()

    def test_fixed_plan_and_unique_credentials_survive_resume(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        initial = copy.deepcopy(state.data)
        serials = [s for listing in initial["listings"] for s in listing["serials"]]
        self.assertEqual(len(set(serials)), len(serials))
        self.assertEqual([r["quantity"] for r in initial["listings"]], planned_quantities())
        self.assertTrue(all(10 <= r["quantity"] <= 50 for r in initial["listings"]))
        self.assertEqual(len({a["password"] for a in initial["accounts"]}), 15)
        self.assertEqual(state.path.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o700)
        state.close()
        resumed = State(self.path, "http://localhost:9000", "a")
        self.assertEqual(resumed.data, initial)
        resumed.close()

    def test_parallel_seed_is_rejected(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        with self.assertRaisesRegex(ValueError, "tiến trình"):
            State(self.path, "http://localhost:9000", "a", create=True)
        state.close()

    def test_invalid_state_releases_lock(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        state.close()
        with self.assertRaisesRegex(ValueError, "khác URL"):
            State(self.path, "http://localhost:9000", "b")
        resumed = State(self.path, "http://localhost:9000", "a")
        resumed.close()

    def test_receipt_missing_from_api_never_repeats_post(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        state.mark("listing", "pending")
        api = unittest.mock.Mock()
        with self.assertRaisesRegex(RuntimeError, "đối soát"):
            create(state, "listing", api, "/api/v1/inventory/equipment-listings", {}, lambda: None, lambda r: None)
        api.request.assert_not_called()
        state.close()

    def test_timeout_after_commit_reconciles_without_second_post(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        committed = {"id": "listing-1", "name": "Máy đầm cóc"}
        rows = []
        api = unittest.mock.Mock()
        def write(*args):
            rows.append(committed)
            raise UncertainWrite("Mất phản hồi sau commit")
        api.request.side_effect = write
        result = create(state, "listing", api, "/api/v1/inventory/equipment-listings", {},
                        lambda: rows[0] if rows else None, lambda r: equal(r, {"name": "Máy đầm cóc"}, "listing"))
        self.assertEqual(result, committed)
        self.assertEqual(api.request.call_count, 1)
        self.assertEqual(state.data["operations"]["listing"]["status"], "done")
        state.close()

    def test_conflicting_record_is_not_adopted(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        api = unittest.mock.Mock()
        with self.assertRaisesRegex(RuntimeError, "không khớp"):
            create(state, "category", api, "/api/v1/catalog/categories", {},
                   lambda: {"id": "c", "description": "Dữ liệu khác"},
                   lambda r: equal(r, {"description": "Dữ liệu demo"}, "category"))
        api.request.assert_not_called()
        state.close()

    def test_successful_write_waits_for_read_model_without_reposting(self):
        state = State(self.path, "http://localhost:9000", "a", create=True)
        record = {"id": "listing-1", "name": "Máy đầm cóc"}
        api = unittest.mock.Mock()
        lookup = unittest.mock.Mock(side_effect=[None, None, record])
        with patch("provision.time.sleep"):
            result = create(state, "listing", api, "/api/v1/inventory/equipment-listings", {}, lookup,
                            lambda r: equal(r, {"name": "Máy đầm cóc"}, "listing"))
        self.assertEqual(result, record)
        self.assertEqual(api.request.call_count, 1)
        self.assertEqual(lookup.call_count, 3)
        state.close()


class FixtureTests(unittest.TestCase):
    def setUp(self):
        self.data, _ = load(ROOT)

    def test_full_manifest(self):
        self.assertEqual(sum(len(t["models"]) for t in self.data["types"]), 100)

    def test_accentless_labels_are_rejected(self):
        self.data["types"][0]["schema"][0]["label"] = "Cong suat"
        with self.assertRaisesRegex(ValueError, "có dấu"):
            validate(self.data, ROOT)

    def test_missing_source_is_rejected(self):
        self.data["types"][0]["models"][0]["sourceFacts"] = []
        with self.assertRaisesRegex(ValueError, "nguồn"):
            validate(self.data, ROOT)

    def test_oversized_machine_is_rejected(self):
        row = next(t for t in self.data["types"] if t["key"] == "diesel-excavator")["models"][0]
        row["specValues"]["operating_weight"] = 9000
        next(f for f in row["sourceFacts"] if f["key"] == "operating_weight")["value"] = 9000
        with self.assertRaises(ValueError):
            validate(self.data, ROOT)

    def test_image_checksum_is_required(self):
        self.data["imageChecksums"][self.data["types"][0]["images"][0]] = "0" * 64
        with self.assertRaisesRegex(ValueError, "Checksum"):
            validate(self.data, ROOT)


class HttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.calls = []
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_POST(self):
                cls.calls.append((self.command, self.path, self.headers, self.rfile.read(int(self.headers.get("Content-Length", 0)))))
                if self.path == "/api/v1/auth/login":
                    self.send_response(200)
                    self.send_header("Set-Cookie", "accessToken=test-token; HttpOnly; Path=/; Secure")
                    self.end_headers()
                    self.wfile.write(b'{}')
                else:
                    self.connection.shutdown(socket.SHUT_RDWR)
                    self.connection.close()
            def do_GET(self):
                cls.calls.append((self.command, self.path, self.headers, None))
                if self.path == "/api/v1/redirect":
                    self.send_response(302)
                    self.send_header("Location", "https://example.com/")
                    self.end_headers()
                    return
                if self.path.startswith("/api/v1/pages"):
                    page = 2 if "page=2" in self.path else 1
                    rows = [{"id": i} for i in range(100)] if page == 1 else [{"id": 100}]
                    payload = {"items": rows, "totalCount": 101}
                else:
                    payload = {"name": "Máy khoan bê tông", "hasBearer": self.headers.get("Authorization") == "Bearer test-token"}
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.url = "http://127.0.0.1:" + str(cls.server.server_address[1])

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    def test_secure_cookie_is_used_as_bearer_on_local_test_gateway(self):
        api = Api(self.url, "user@example.test", "private-password", interval=0)
        row = api.request("GET", "/api/v1/test")
        self.assertEqual(row["name"], "Máy khoan bê tông")
        self.assertTrue(row["hasBearer"])

    def test_external_http_and_embedded_credentials_are_rejected(self):
        for url in ("http://example.com", "https://user:secret@example.com"):
            with self.assertRaises(ValueError):
                Api(url)

    def test_anonymous_read_does_not_send_login_cookie_or_bearer(self):
        api = Api(self.url, "user@example.test", "private-password", interval=0)
        api.login()
        row = api.request("GET", "/api/v1/test", authenticated=False)
        self.assertFalse(row["hasBearer"])
        self.assertIsNone(self.calls[-1][2].get("Cookie"))

    def test_credentials_never_follow_redirect(self):
        api = Api(self.url, interval=0)
        api.token = "test-token"
        with self.assertRaises(ApiError):
            api.request("GET", "/api/v1/redirect")

    def test_unacknowledged_post_is_not_retried(self):
        api = Api(self.url, interval=0)
        count = len(self.calls)
        with self.assertRaises(UncertainWrite):
            api.request("POST", "/api/v1/timeout", {"name": "Máy trộn vữa"})
        self.assertEqual(len(self.calls), count + 1)

    def test_pagination_reads_last_page(self):
        api = Api(self.url, interval=0)
        self.assertEqual(len(api.pages("/api/v1/pages")), 101)


class RentalRateTests(unittest.TestCase):
    def test_fixture_rate_remains_default(self):
        self.assertEqual(rental_rate({}, {"dailyRate": 120000}),
                         {"dailyRate": 120000, "weeklyRate": 720000, "monthlyRate": 2640000, "currency": "VND"})

    def test_approved_rate_is_used_without_mutating_plan(self):
        plan = {"rentalRate": {"dailyRate": 130000, "weeklyRate": 720000, "monthlyRate": 2640000, "currency": "VND"}}
        actual = rental_rate(plan, {"dailyRate": 120000})
        self.assertEqual(actual, plan["rentalRate"])
        actual["dailyRate"] = 1
        self.assertEqual(plan["rentalRate"]["dailyRate"], 130000)

    def test_invalid_rate_is_rejected(self):
        for value in (0, -1, True, "130000", float("nan"), float("inf")):
            with self.subTest(value=value), self.assertRaises(RuntimeError):
                rental_rate({"rentalRate": {"dailyRate": value, "weeklyRate": 720000, "monthlyRate": 2640000, "currency": "VND"}}, {"dailyRate": 120000})
        with self.assertRaises(RuntimeError):
            rental_rate({"rentalRate": {"dailyRate": 130000}}, {"dailyRate": 120000})


if __name__ == "__main__":
    unittest.main()
