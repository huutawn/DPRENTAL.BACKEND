#!/usr/bin/env python3
"""Check or update pinned, unmodified RecommendationService schema test fixtures."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / 'InventoryService/tests/Api.Tests/Contracts/Recommendation'
FILES = ('listing-viewed.schema.json', 'search-performed.schema.json')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, help='Canonical RecommendationService Git checkout')
    parser.add_argument('--revision', help='Commit to pin when updating')
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--check', action='store_true')
    mode.add_argument('--update', action='store_true')
    args = parser.parse_args()
    if args.update and (args.source is None or args.revision is None):
        parser.error('--update requires --source and --revision')
    if args.update:
        revision = subprocess.check_output(['git', '-C', str(args.source), 'rev-parse', args.revision + '^{commit}'], text=True).strip()
        manifest = {'owner': 'RecommendationService', 'revision': revision, 'files': {}}
        RESOURCES.mkdir(parents=True, exist_ok=True)
        for name in FILES:
            source_path = 'docs/contracts/schemas/' + name
            data = subprocess.check_output(['git', '-C', str(args.source), 'show', revision + ':' + source_path])
            (RESOURCES / name).write_bytes(data)
            manifest['files'][name] = {'sourcePath': source_path, 'sha256': digest(data)}
        (RESOURCES / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    manifest = json.loads((RESOURCES / 'manifest.json').read_text())
    if set(manifest['files']) != set(FILES):
        raise SystemExit('Unexpected contract fixture set')
    for name, info in manifest['files'].items():
        data = (RESOURCES / name).read_bytes()
        if digest(data) != info['sha256']:
            raise SystemExit('Fixture checksum mismatch: ' + name)
        if args.source:
            canonical = subprocess.check_output(['git', '-C', str(args.source), 'show', manifest['revision'] + ':' + info['sourcePath']])
            if data != canonical:
                raise SystemExit('Canonical schema mismatch: ' + name)
    print('Recommendation contract fixtures match pinned checksums' + (' and canonical source.' if args.source else '.'))


if __name__ == '__main__':
    main()
