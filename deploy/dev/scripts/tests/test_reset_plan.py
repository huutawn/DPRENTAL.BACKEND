"""Exercise the actual embedded Compose planner without touching Docker."""
import json
from pathlib import Path
import subprocess
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'reset-data.sh'
SOURCE = SCRIPT.read_text().split("config --format json | python3 -c '\n", 1)[1].split("\n') || fail", 1)[0]


def configuration():
    names = ['postgres-data', 'integrity-postgres-data', 'media-minio-data', 'valkey-data', 'kafka-data']
    return {'name': 'dprental-dev', 'volumes': {n: {'name': 'dprental-dev_' + n} for n in names},
            'services': {'postgres': {'image': 'postgres:17'}, 'integrity-migrate': {},
                         'payment-migrate': {'depends_on': {'integrity-migrate': {}}}}}


class PlannerTests(unittest.TestCase):
    def run_plan(self, config, env_override=None):
        import os
        env = os.environ.copy()
        if env_override:
            env.update(env_override)
        return subprocess.run(['python3', '-c', SOURCE], input=json.dumps(config), text=True, capture_output=True, env=env)
    def test_integrity_volume_and_dependency_order(self):
        result = self.run_plan(configuration())
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('dprental-dev_integrity-postgres-data', result.stdout)
        self.assertLess(result.stdout.index('integrity-migrate'), result.stdout.index('payment-migrate'))

    def test_unknown_storage_rejected(self):
        c = configuration()
        c['volumes']['future-data'] = {}
        self.assertNotEqual(self.run_plan(c).returncode, 0)

    def test_external_data_rejected(self):
        c = configuration()
        c['volumes']['postgres-data']['external'] = True
        self.assertNotEqual(self.run_plan(c).returncode, 0)

    def test_wrong_project_rejected(self):
        c = configuration()
        c['name'] = 'production'
        self.assertNotEqual(self.run_plan(c).returncode, 0)

    def test_migration_cycle_rejected(self):
        c = configuration()
        c['services']['integrity-migrate']['depends_on'] = {'payment-migrate': {}}
        self.assertNotEqual(self.run_plan(c).returncode, 0)

    def test_anonymous_volume_rejected(self):
        c = configuration()
        c['services']['postgres']['volumes'] = [{'type': 'volume', 'target': '/data'}]
        self.assertNotEqual(self.run_plan(c).returncode, 0)

    def test_unclassified_bind_rejected(self):
        c = configuration()
        c['services']['postgres']['volumes'] = [{'type': 'bind', 'source': '/tmp/unknown', 'read_only': True}]
        self.assertNotEqual(self.run_plan(c).returncode, 0)

    def test_volume_driver_options_rejected(self):
        c = configuration()
        c['volumes']['postgres-data']['driver_opts'] = {'device': '/host'}
        self.assertNotEqual(self.run_plan(c).returncode, 0)
    def test_e2e_project_prefix_accepted(self):
        c = configuration()
        c['name'] = 'dprental-e2e-run1'
        names = ['postgres-data', 'integrity-postgres-data', 'media-minio-data', 'valkey-data', 'kafka-data']
        c['volumes'] = {n: {'name': 'dprental-e2e-run1_' + n} for n in names}
        result = self.run_plan(c, {'DPR_PROJECT': 'dprental-e2e-run1'})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('dprental-e2e-run1', result.stdout)

    def test_arbitrary_project_prefix_rejected(self):
        c = configuration()
        c['name'] = 'custom-project-123'
        result = self.run_plan(c, {'DPR_PROJECT': 'custom-project-123'})
        self.assertNotEqual(result.returncode, 0)

    def test_meilisearch_six_volumes_accepted(self):
        c = configuration()
        c['services']['meilisearch'] = {'image': 'meilisearch:v1.55.0'}
        c['volumes']['meilisearch-data'] = {'name': 'dprental-dev_meilisearch-data'}
        result = self.run_plan(c)
        self.assertEqual(result.returncode, 0, result.stderr)
        lines = result.stdout.strip().splitlines()
        self.assertEqual(lines[1], '6')
        self.assertIn('dprental-dev_meilisearch-data', lines)

    def test_unknown_secret_rejected(self):
        c = configuration()
        c['secrets'] = {'unknown_secret': {'file': '/tmp/test'}}
        self.assertNotEqual(self.run_plan(c).returncode, 0)
