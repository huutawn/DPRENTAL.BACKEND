"""Unit tests for E2E scenarios, state machine, schedule limits, and safe webhook simulation."""
import datetime
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock

ROOT = Path(__file__).resolve().parents[1] / "seed-data"
sys.path.insert(0, str(ROOT))

from api import Api, ApiError
from e2e_scenarios import E2EScenarioRunner, check_schedule_limit, now_vn, today_vn
from state import State


class E2EScenarioTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "state"

    def tearDown(self):
        self.tmp.cleanup()

    def test_schedule_exceeding_14_days_is_rejected(self):
        start = datetime.date(2026, 11, 5)
        end = start + datetime.timedelta(days=15)
        with self.assertRaisesRegex(ValueError, "vượt quá giới hạn tối đa 14 ngày"):
            check_schedule_limit(start, end, max_days=14)

    def test_schedule_within_14_days_passes(self):
        start = datetime.date(2026, 11, 5)
        end = start + datetime.timedelta(days=10)
        check_schedule_limit(start, end, max_days=14)

    def test_state_scenario_recording_and_waiting(self):
        state = State(self.path, "http://localhost:9000", "fingerprint-1", project="dprental-dev", create=True)
        state.mark_scenario("SCENARIO_8_HANDOVER_RETURN", "waiting",
                            next_eligible_at="2026-11-08T00:00:00+07:00",
                            detail={"itemId": "item-123"},
                            audit=["Waiting for expected start date"])
        sc = state.get_scenario("SCENARIO_8_HANDOVER_RETURN")
        self.assertEqual(sc["status"], "waiting")
        self.assertEqual(sc["nextEligibleAt"], "2026-11-08T00:00:00+07:00")
        self.assertEqual(sc["detail"]["itemId"], "item-123")
        state.close()

    def test_state_intent_recording_and_clearing(self):
        state = State(self.path, "http://localhost:9000", "fingerprint-1", project="dprental-dev", create=True)
        state.record_intent("OP-CHECKOUT-1", {"order": "test"})
        self.assertIn("OP-CHECKOUT-1", state.data["intents"])
        state.clear_intent("OP-CHECKOUT-1")
        self.assertNotIn("OP-CHECKOUT-1", state.data["intents"])
        state.close()

    def test_old_dataset_id_rejected_on_resume(self):
        state = State(self.path, "http://localhost:9000", "fingerprint-1", project="dprental-dev", create=True)
        state.close()
        # Modifying state to simulate different datasetId
        state_file = self.path / "state.json"
        data = json.loads(state_file.read_text(encoding="utf-8"))
        data["datasetId"] = "different-dataset-id"
        state_file.write_text(json.dumps(data), encoding="utf-8")

        with self.assertRaisesRegex(ValueError, "datasetId cũ"):
            State(self.path, "http://localhost:9000", "fingerprint-1", project="dprental-dev")

    def test_arbitrary_project_rejected_in_state(self):
        with self.assertRaisesRegex(ValueError, "Project 'prod-cluster' không hợp lệ"):
            State(self.path, "http://localhost:9000", "fingerprint-1", project="prod-cluster", create=True)

    def test_e2e_project_accepted_in_state(self):
        state = State(self.path, "http://localhost:9000", "fingerprint-1", project="dprental-e2e-run-42", create=True)
        self.assertEqual(state.project, "dprental-e2e-run-42")
        state.close()

    def test_webhook_simulation_rejects_production_host(self):
        api = Api("https://dprental.lazyops.cloud")  # Non-dev host
        with self.assertRaisesRegex(ValueError, "không phải môi trường dev/e2e"):
            api.simulate_sepay_webhook("dprental-dev", "key-123", {"content": "test"})

    def test_webhook_simulation_rejects_invalid_project(self):
        api = Api("http://localhost:9000")
        with self.assertRaisesRegex(ValueError, "Không được mô phỏng webhook trên project: custom-proj"):
            api.simulate_sepay_webhook("custom-proj", "key-123", {"content": "test"})


if __name__ == "__main__":
    unittest.main()
