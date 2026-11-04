"""Run the real deployment script with Docker/HTTP boundaries simulated; no daemon access."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

SERVICE = 'notification'
NAME = 'notificationservice'
IMAGES = {'NOTIFICATION_SERVICE_IMAGE': 'dpnotification-notification', 'NOTIFICATION_MIGRATOR_IMAGE': 'dpnotification-notification-migrator'}
ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts' / f'deploy-{NAME}.sh'
SHA = 'a' * 40

DOCKER = r'''#!/usr/bin/env python3
import json, os, pathlib, sys, time
a = sys.argv[1:]
root = pathlib.Path(os.environ['DPR_DEPLOY_DIRECTORY'])
with open(root / 'calls', 'a') as out:
    out.write(json.dumps({'args': a, 'release': (root / 'release.env').read_text(),
                          'config': os.environ.get('DOCKER_CONFIG')}) + '\n')
failure = os.environ.get('FAILURE', '')
def fail(name):
    if failure == name: sys.exit(7)
if a[0] == 'login':
    assert sys.stdin.readline().strip() == 'fake-token'
    fail('login')
if a[0] == 'inspect' and ':' in a[2]:
    print('exited:1' if failure == 'completed' else 'exited:0')
    sys.exit(0)
if a[0] == 'inspect':
    print('unhealthy' if failure == 'health' and a[-1] in os.environ['TEST_CONTAINERS'].split(',') else 'healthy')
if 'config' in a:
    fail('config')
if 'pull' in a:
    fail('pull')
    if failure == 'slow': time.sleep(0.6)
if 'ps' in a:
    if failure != 'dependency': print(a[-1] + '-container')
if 'psql' in a:
    fail('schema')
    print({'no-schema': '', 'invalid-schema': 'surprise'}.get(failure, '1'))
if 'pg_dump' in a or any('exec pg_dump' in x for x in a):
    fail('backup')
    if failure != 'empty-backup': print('custom archive')
if 'pg_restore' in a:
    fail('archive')
if 'stop' in a: fail('stop')
if 'run' in a:
    fail('migration')
    if a[-1] == 'integrity-migrate': fail('integrity-migration')
if 'up' in a:
    fail('recreate')
    if failure == 'drift':
        release = root / 'release.env'
        release.write_text(release.read_text() + 'EXTERNAL_IMAGE=changed\n')
if 'exec' in a and ('wget' in a or 'perl' in a):
    fail('internal')
    if any('jwks.json' in x for x in a): fail('jwks')
'''
CURL = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
with open(pathlib.Path(os.environ['DPR_DEPLOY_DIRECTORY']) / 'http', 'a') as out:
    out.write(json.dumps(sys.argv[1:]) + '\n')
failure = os.environ.get('FAILURE', '')
if failure == 'public': sys.exit(22)
if failure == 'dns': sys.exit(6)
if failure == 'public-api' and '/api/' in sys.argv[-1]: sys.exit(22)
if '/api/v1/notifications/unread-count' in sys.argv[-1]: print('401', end='')
'''


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.original = 'OTHER_IMAGE=untouched\n' + ''.join(
            f'{key}=ghcr.io/example/{image}:previous\n' for key, image in IMAGES.items())
        (self.root / 'release.env').write_text(self.original)
        (self.root / '.env').write_text('DEV_DOMAIN=api.example.test\nPOSTGRES_USER=test\nPOSTGRES_DB=test\n')
        (self.root / 'compose.yaml').write_text('services: {}\n')
        for name, script in [('docker', DOCKER), ('curl', CURL)]:
            (self.root / name).write_text(script)
            (self.root / name).chmod(0o755)
        self.env = dict(os.environ, PATH=f'{self.root}:{os.environ["PATH"]}',
                        DPR_DEPLOY_DIRECTORY=str(self.root), GHCR_IMAGE_NAMESPACE='example',
                        GHCR_USERNAME='test', GHCR_TOKEN='fake-token', TEST_CONTAINERS='notification-service-container,integrity-host-container',
                        **{f'{SERVICE.upper()}_DEPLOY_HEALTH_ATTEMPTS': '1',
                           f'{SERVICE.upper()}_DEPLOY_HEALTH_INTERVAL_SECONDS': '0'})

    def run_deploy(self, failure='', sha=SHA):
        return subprocess.run(['bash', str(SCRIPT), sha], env=dict(self.env, FAILURE=failure),
                              capture_output=True, text=True, timeout=15)

    def calls(self):
        return [json.loads(line) for line in (self.root / 'calls').read_text().splitlines()] if (self.root / 'calls').exists() else []

    def assert_credentials_removed(self, result):
        self.assertNotIn('fake-token', result.stdout + result.stderr)
        for call in self.calls():
            self.assertNotIn('fake-token', str(call))
            if call['config']:
                self.assertFalse(Path(call['config']).exists())

    def test_success_preserves_mapping_order_and_smokes(self):
        result = self.run_deploy()
        self.assertEqual(0, result.returncode, result.stderr)
        release = (self.root / 'release.env').read_text()
        self.assertIn('OTHER_IMAGE=untouched', release)
        for key, image in IMAGES.items():
            self.assertIn(f'{key}=ghcr.io/example/{image}:{SHA}', release)
        calls = self.calls()
        self.assertTrue(all(call['release'] == self.original for call in calls))
        args = [' '.join(call['args']) for call in calls]
        pull = next(i for i, a in enumerate(args) if ' pull ' in a)
        config = next(i for i, a in enumerate(args) if ' config ' in a)
        self.assertLess(config, pull)
        up = [i for i, a in enumerate(args) if ' up ' in a]
        self.assertTrue(up)
        expected_runtime = ['integrity-host', 'payment-service'] if SERVICE == 'payment' else [
            'gateway' if SERVICE == 'gateway' else SERVICE + '-service']
        self.assertEqual(expected_runtime, [calls[i]['args'][-1] for i in up])
        self.assertTrue(all('--no-deps' in args[i] and '--pull never' in args[i] for i in up))
        if SERVICE != 'gateway':
            migration = [i for i, a in enumerate(args) if ' run ' in a]
            backup = next(i for i, a in enumerate(args) if 'pg_dump' in a)
            self.assertLess(pull, backup)
            self.assertLess(backup, migration[0])
            self.assertLess(migration[-1], up[0])
            self.assertTrue(all('--no-deps --pull never' in args[i] for i in migration))
            if SERVICE == 'payment':
                self.assertIn('payment-migrate', args[migration[0]])
                self.assertIn('integrity-migrate', args[migration[1]])
                self.assertIn('integrity-host', args[up[0]])
                self.assertIn('payment-service', args[up[1]])
                self.assertTrue(any('integrity-postgres' in a and 'pg_dump' in a for a in args))
            if SERVICE in ('catalog', 'inventory'):
                self.assertLess(next(i for i, a in enumerate(args) if ' stop ' in a), backup)
            if SERVICE in ('auth', 'notification', 'recommendation', 'payment'):
                self.assertTrue(any('jwks.json' in a for a in args))
        else:
            self.assertFalse(any(' run ' in a or 'pg_dump' in a for a in args))
        http = (self.root / 'http').read_text()
        self.assertIn('/health/ready', http)
        if SERVICE == 'catalog':
            self.assertIn('/categories', http)
            self.assertIn('/equipment-types', http)
        if SERVICE == 'gateway': self.assertIn('/brands', http)
        self.assertFalse((self.root / '.deployment-pending').exists())
        journal = next((self.root / '.deployments').iterdir())
        self.assertEqual(self.original, (journal / 'previous.env').read_text())
        self.assertEqual('success\n', (journal / 'result').read_text())
        self.assertEqual(0, self.run_deploy().returncode)
        self.assertEqual(0o600, (self.root / 'release.env').stat().st_mode & 0o777)
        self.assert_credentials_removed(result)

    def test_failure_keeps_previous_release_and_blocks_unsafe_retry(self):
        cases = ['login', 'config', 'pull', 'dependency', 'recreate', 'health', 'internal', 'public', 'dns']
        if SERVICE != 'gateway': cases += ['backup', 'empty-backup', 'archive', 'migration']
        if SERVICE in ('catalog', 'inventory', 'media'): cases += ['schema', 'invalid-schema']
        if SERVICE in ('catalog', 'inventory'): cases += ['stop']
        if SERVICE in ('catalog', 'gateway'): cases += ['public-api']
        if SERVICE in ('auth', 'notification', 'recommendation', 'payment'): cases += ['jwks']
        if SERVICE == 'payment': cases += ['integrity-migration']
        if SERVICE == 'media': cases += ['completed']
        for failure in cases:
            with self.subTest(failure=failure):
                # Separate attempt/state for each scenario, as an operator would use a fresh VM fixture.
                self.tearDown_fixture()
                result = self.run_deploy(failure)
                self.assertNotEqual(0, result.returncode, failure)
                self.assertEqual(self.original, (self.root / 'release.env').read_text())
                self.assert_credentials_removed(result)
                args = [' '.join(c['args']) for c in self.calls()]
                if failure in ('login', 'config', 'pull', 'dependency', 'backup', 'empty-backup', 'archive', 'schema', 'invalid-schema', 'stop'):
                    self.assertFalse(any(' run ' in a or ' up ' in a for a in args))
                if failure in ('migration', 'integrity-migration'):
                    self.assertFalse(any(' up ' in a for a in args))
                pending = self.root / '.deployment-pending'
                if pending.exists():
                    before = len(self.calls())
                    retry = self.run_deploy()
                    self.assertNotEqual(0, retry.returncode)
                    self.assertEqual(before, len(self.calls()))
                if failure == 'dns': self.assertIn('DNS/proxy', result.stderr)

    def tearDown_fixture(self):
        self.tmp.cleanup()
        self.setUp()

    def test_boundary_inputs_rejected_before_docker(self):
        for sha in ['latest', 'a' * 39, 'a' * 41, 'A' * 40, '../' + SHA, SHA + ';id']:
            self.assertNotEqual(0, self.run_deploy(sha=sha).returncode)
        self.assertEqual([], self.calls())
        for key in ['GHCR_TOKEN', 'GHCR_USERNAME', 'GHCR_IMAGE_NAMESPACE']:
            saved = self.env.pop(key)
            self.assertNotEqual(0, self.run_deploy().returncode)
            self.env[key] = saved
        self.assertEqual([], self.calls())

    def test_environment_sha_fallback_and_missing_files(self):
        result = subprocess.run(['bash', str(SCRIPT)], env=dict(self.env, GITHUB_SHA=SHA),
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        for name in ['.env', 'compose.yaml', 'release.env']:
            self.tearDown_fixture()
            (self.root / name).unlink()
            self.assertNotEqual(0, self.run_deploy().returncode)
            self.assertEqual([], self.calls())

    def test_wrong_namespace_and_duplicate_mapping_fail_before_pull(self):
        for replacement in [self.original.replace('ghcr.io/example/', 'ghcr.io/evil/'),
                            self.original + self.original]:
            self.tearDown_fixture()
            (self.root / 'release.env').write_text(replacement)
            result = self.run_deploy()
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(any('pull' in c['args'] for c in self.calls()))
            self.assertEqual(replacement, (self.root / 'release.env').read_text())

    def test_external_state_drift_is_not_overwritten(self):
        result = self.run_deploy('drift')
        self.assertNotEqual(0, result.returncode)
        release = (self.root / 'release.env').read_text()
        self.assertIn('EXTERNAL_IMAGE=changed', release)
        self.assertNotIn(':' + SHA, release)
        self.assertTrue((self.root / '.deployment-pending').exists())

    def test_same_host_lock_serializes_transactions(self):
        first = subprocess.Popen(['bash', str(SCRIPT), SHA], env=dict(self.env, FAILURE='slow'),
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(lambda: first.poll() is None and first.kill())
        deadline = time.monotonic() + 5
        while not (self.root / '.deployment-pending').exists() and time.monotonic() < deadline:
            time.sleep(0.01)
        second_sha = 'b' * 40
        second = subprocess.Popen(['bash', str(SCRIPT), second_sha], env=self.env,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(lambda: second.poll() is None and second.kill())
        first_output = first.communicate(timeout=15)
        second_output = second.communicate(timeout=15)
        self.assertEqual(0, first.returncode, first_output)
        self.assertEqual(0, second.returncode, second_output)
        calls = self.calls()
        logins = [i for i, c in enumerate(calls) if c['args'][0] == 'login']
        self.assertEqual(2, len(logins))
        self.assertNotIn(':previous', calls[logins[1]]['release'])


if __name__ == '__main__':
    unittest.main()
