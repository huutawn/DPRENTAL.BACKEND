"""Privilege-boundary tests; never invoke sudo, Docker or GitHub."""
import contextlib
import io
import json
import os
from pathlib import Path
import runpy
import stat
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch

CODE = runpy.run_path(str(Path(__file__).resolve().parents[1] / 'dprental-deploy'))
GLOBALS = CODE['main'].__globals__
SHA = 'a' * 40


class SecurityTests(unittest.TestCase):
    def test_argument_allowlist(self):
        for name in CODE['SERVICES']:
            self.assertEqual([name, SHA], CODE['validate_arguments']([name, SHA]))
        for args in [[], ['auth'], ['../auth', SHA], ['auth', SHA, 'extra'],
                     ['auth', 'latest'], ['auth', 'a' * 39], ['auth', 'A' * 40], ['auth', SHA + ';id']]:
            with self.assertRaises(ValueError): CODE['validate_arguments'](args)

    def test_trust_rejects_owner_write_permissions_symlink_and_acl(self):
        for uid, mode, acl in [(1000, stat.S_IFREG | 0o644, []),
                               (0, stat.S_IFREG | 0o664, []),
                               (0, stat.S_IFLNK | 0o777, []),
                               (0, stat.S_IFREG | 0o644, ['system.posix_acl_access']),
                               (0, stat.S_IFDIR | 0o755, ['system.posix_acl_default'])]:
            with patch.object(Path, 'lstat', return_value=SimpleNamespace(st_uid=uid, st_mode=mode)), \
                    patch('os.listxattr', return_value=acl), self.assertRaises(ValueError):
                CODE['trusted_path'](Path('/trusted/path'))
        CODE['trusted_path'](Path('/usr'))  # Real root-owned filesystem boundary.

    def test_compose_rejects_host_privileges(self):
        for field, value in [('privileged', True), ('cap_add', ['SYS_ADMIN']),
                             ('devices', ['/dev/sda']), ('device_cgroup_rules', ['a *:* rwm']),
                             ('volumes_from', ['host']), ('pid', 'host'), ('ipc', 'host'),
                             ('network_mode', 'host'), ('use_api_socket', True)]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                CODE['validate_compose']({'services': {'test': {field: value}}})
        for mount in [{'type': 'bind', 'source': '/var/run/docker.sock'},
                      {'type': 'bind', 'source': 'relative', 'read_only': True},
                      {'type': 'unknown'}]:
            with self.assertRaises(ValueError):
                CODE['validate_compose']({'services': {'test': {'volumes': [mount]}}})
        with self.assertRaises(ValueError):
            CODE['validate_compose']({'volumes': {'data': {'driver_opts': {'device': '/'}}}})
        with patch('subprocess.run', return_value=SimpleNamespace(
                returncode=0, stdout=b'[{"Driver":"local","Options":{"device":"/"}}]')), \
                self.assertRaises(ValueError):
            CODE['validate_compose']({'volumes': {'data': {'name': 'data'}}})

    def test_bind_and_secret_files_are_checked(self):
        calls = []
        with patch.dict(GLOBALS, trusted_path=lambda p: calls.append(p)):
            CODE['validate_compose']({'services': {'test': {'volumes': [
                {'type': 'bind', 'source': '/trusted-file', 'read_only': True}]}},
                'secrets': {'key': {'file': '/secret'}}, 'configs': {'config': {'file': '/config'}}})
        self.assertEqual([Path('/trusted-file'), Path('/secret'), Path('/config')], calls)

    def test_entrypoint_cleans_environment(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            root.chmod(0o700)
            (root / 'namespace').write_text('example')
            executed = []
            replacements = dict(DEPLOY=root, CONFIG=root, LIBEXEC=root,
                                trusted_path=lambda p: None, trusted_tree=lambda p: None,
                                compose_preflight=lambda env: None)
            with patch.dict(GLOBALS, replacements), patch('os.geteuid', return_value=0), \
                    patch('os.chdir'), patch('os.umask'), \
                    patch('os.execve', side_effect=lambda *args: executed.append(args)), \
                    patch.dict(os.environ, {'BASH_ENV': '/evil', 'DOCKER_HOST': 'tcp://evil', 'PYTHONPATH': '/evil', 'SSL_CERT_FILE': '/evil'}), \
                    patch.object(__import__('sys'), 'argv', ['wrapper', 'auth', SHA]):
                tokens = ['fake_token', 'opaque.token-with-+/=~', 'a' * 8192]
                for token in tokens:
                    with patch('sys.stdin', io.StringIO('test\n' + token + '\n')):
                        CODE['main']()
                    self.assertEqual(token, executed[-1][2]['GHCR_TOKEN'])
                self.assertEqual(len(tokens), len(executed))
                for token in ['', 'white space', 'bad\x00control', 'bad\x7fcontrol', 'bad\rheader', 'unicode-ệ', 'a' * 8193]:
                    with patch('sys.stdin', io.StringIO('test\n' + token + '\n')):
                        with self.assertRaises(ValueError): CODE['main']()
                self.assertEqual(len(tokens), len(executed))
                executable, arguments, env = executed[0]
                self.assertEqual('/bin/bash', executable)
                self.assertEqual(['bash', str(root / 'deploy-authservice.sh'), SHA], arguments)
                self.assertEqual('/usr/sbin:/usr/bin:/sbin:/bin', env['PATH'])
                self.assertFalse(set(env) & {'BASH_ENV', 'DOCKER_HOST', 'PYTHONPATH', 'SHELLOPTS', 'COMPOSE_FILE'})



if __name__ == '__main__':
    unittest.main()
