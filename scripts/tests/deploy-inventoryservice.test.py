"""Exercise deployment orchestration with controlled infrastructure responses."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'deploy-inventoryservice.sh'
SHA = 'a' * 40


class DeploymentTests(unittest.TestCase):
    def run_deploy(self, failure='', sha=SHA):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / '.env').write_text('POSTGRES_USER=authservice\nPOSTGRES_DB=authservice\nDEV_DOMAIN=example.test\n')
            (root / 'release.env').write_text('OTHER_IMAGE=untouched\nINVENTORY_SERVICE_IMAGE=ghcr.io/test/dprental-inventory:old\nINVENTORY_MIGRATOR_IMAGE=ghcr.io/test/dprental-inventory-migrator:old\n')
            (root / 'compose.yaml').write_text('services: {}\n')
            (root / 'docker').write_text('''#!/bin/bash
printf '%s\\n' "$*" >> "$DEPLOY_LOG"
case "$*" in
  *psql*) echo 1 ;;
  *pg_dump*) [ "$FAILURE" != backup ] || exit 7; echo backup ;;
  *'run --rm inventory-migrate'*) [ "$FAILURE" != migration ] || exit 8 ;;
  *'ps -q inventory-service'*) echo inventory-container ;;
  inspect*) echo healthy ;;
esac
''')
            (root / 'curl').write_text('#!/bin/bash\nexit 0\n')
            for name in ['docker', 'curl']:
                (root / name).chmod(0o755)
            env = dict(os.environ, PATH=f'{root}:{os.environ["PATH"]}',
                       DPR_DEPLOY_DIRECTORY=str(root), GHCR_USERNAME='test', GHCR_TOKEN='fake',
                       DEPLOY_LOG=str(root / 'calls'), FAILURE=failure)
            result = subprocess.run(['bash', str(SCRIPT), sha], env=env, capture_output=True, text=True)
            calls = (root / 'calls').read_text() if (root / 'calls').exists() else ''
            release = (root / 'release.env').read_text()
            return result, calls, release

    def test_success_preserves_other_images_and_orders_backup_before_migration(self):
        result, calls, release = self.run_deploy()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('OTHER_IMAGE=untouched', release)
        self.assertIn('dprental-inventory:' + SHA, release)
        self.assertIn('dprental-inventory-migrator:' + SHA, release)
        self.assertLess(calls.index('pg_dump'), calls.index('run --rm inventory-migrate'))
        self.assertLess(calls.index('run --rm inventory-migrate'), calls.index('--force-recreate inventory-service'))
        self.assertIn('pg_dump -U authservice -d authservice -n inventory -Fc', calls)

    def test_backup_failure_blocks_migration(self):
        result, calls, _ = self.run_deploy('backup')
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn('run --rm inventory-migrate', calls)

    def test_migration_failure_blocks_runtime_restart(self):
        result, calls, _ = self.run_deploy('migration')
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn('--force-recreate inventory-service', calls)

    def test_invalid_sha_rejected_before_docker(self):
        result, calls, release = self.run_deploy(sha='latest')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('', calls)
        self.assertIn(':old', release)


if __name__ == '__main__':
    unittest.main()
