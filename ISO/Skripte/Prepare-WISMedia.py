#!/usr/bin/env python3
"""Pripremi OEM stablo samo iz potpisanog, commit-vezanog WIS izdanja."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT.parent / 'ServiceHub'))
from servishub.releases import ReleaseError, sha256, verify_release


def current_commit(root):
    if subprocess.check_output(['git', '-C', str(root), 'status', '--porcelain'], text=True).strip():
        raise ReleaseError('Medijum zahteva čist Git commit.')
    return subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()


def reject_symlink_path(path, root):
    for p in [path, *path.parents]:
        if p == root.parent:
            break
        if p.is_symlink():
            raise ReleaseError('Media putanja sadrži symlink.')


def promote_directory(stage, target, backups, prefix):
    backup = backups / (prefix + '-' + uuid.uuid4().hex)
    if target.exists():
        os.rename(target, backup)
    try:
        os.rename(stage, target)
    except Exception:
        if backup.exists():
            os.rename(backup, target)
        raise


def verify_media(root):
    root = Path(root)
    attestation = json.loads((root / '.wis-media-attestation.json').read_text())
    if attestation['source_commit'] != current_commit(root):
        raise ReleaseError('OEM medijum ne odgovara trenutnom source commit-u.')
    release = Path(attestation['release_dir'])
    meta = verify_release(release / 'wis-payload.zip', release / 'payload_meta.json',
                          root.parent / 'Launcher/trusted_public_key.xml')
    if meta.get('source_commit') != attestation['source_commit']:
        raise ReleaseError('Potpisano izdanje ne odgovara source commit-u.')
    prefix = 'sources/$OEM$/$1/setup/'
    expected_files = {prefix + k: v for k, v in meta['required_files'].items()}
    expected_files[prefix + 'payload_meta.json'] = sha256(release / 'payload_meta.json')
    expected_files['WIS.Launcher.exe'] = meta['required_files']['WIS.Launcher.exe']
    if 'version.json' in meta['required_files']:
        expected_files['version.json'] = meta['required_files']['version.json']
    for f in (root / 'media/oem/Setup/Scripts').iterdir():
        expected_files['sources/$OEM$/$$/Setup/Scripts/' + f.name] = sha256(f)
    for name, expected in expected_files.items():
        if sha256(root / name) != expected:
            raise ReleaseError('OEM/media resurs izmenjen nakon pripreme: ' + name)
    for f in (root / 'sources/$OEM$/$1/setup').rglob('*'):
        if f.is_symlink():
            raise ReleaseError('OEM symlink nije dozvoljen.')
        if f.is_file() and f.relative_to(root).as_posix() not in expected_files and f.name != 'Wifi-Servis.ini':
            raise ReleaseError('Nepotpisani dodatak OEM paketu: ' + f.name)
    for f in (root / 'sources/$OEM$/$$/Setup/Scripts').rglob('*'):
        if f.is_symlink() or f.is_file() and f.relative_to(root).as_posix() not in expected_files:
            raise ReleaseError('Nepredviđeni dodatak Windows Setup skriptama.')
    if not attestation.get('wifi_config') and (root / 'Wifi-Servis.ini').exists():
        raise ReleaseError('Stari Wi-Fi config nije deo pripremljenog medijuma.')
    if attestation.get('wifi_config') and sha256(root / 'Wifi-Servis.ini') != sha256(root / 'sources/$OEM$/$1/setup/Wifi-Servis.ini'):
        raise ReleaseError('Wi-Fi config korena i OEM kopije se ne poklapaju.')
    return attestation


def prepare_media(root, release_dir, wifi_config=None):
    root, release_dir = Path(root), Path(release_dir)
    wifi_data = Path(wifi_config).read_bytes() if wifi_config else None
    if wifi_data is not None and len(wifi_data) > 64 * 1024:
        raise ReleaseError('Servisni Wi-Fi config je prevelik.')
    commit = current_commit(root)
    meta = verify_release(release_dir / 'wis-payload.zip', release_dir / 'payload_meta.json',
                          root.parent / 'Launcher/trusted_public_key.xml')
    if meta.get('source_commit') != commit:
        raise ReleaseError('Izdanje ne odgovara trenutnom Git commit-u.')
    target = root / 'sources/$OEM$/$1/setup'
    reject_symlink_path(target, root)
    target.parent.mkdir(parents=True, exist_ok=True)
    backup_dir = root.parent / '.workspace/artifacts/media-backups'
    backup_dir.mkdir(parents=True, exist_ok=True)
    backup_dir.chmod(0o700)
    with tempfile.TemporaryDirectory(prefix='.wis-oem-', dir=target.parent) as temp:
        stage = Path(temp)
        # verify_release je prethodno proverio putanje, duplikate, symlink i heševe.
        with zipfile.ZipFile(release_dir / 'wis-payload.zip') as z:
            z.extractall(stage)
        shutil.copy2(release_dir / 'payload_meta.json', stage / 'payload_meta.json')
        if wifi_config:
            (stage / 'Wifi-Servis.ini').write_bytes(wifi_data)
        promote_directory(stage, target, backup_dir, 'setup')
    shutil.copy2(target / 'WIS.Launcher.exe', root / 'WIS.Launcher.exe')
    # Keep the signed release record on the media, including independent component versions.
    if (target / 'version.json').exists():
        shutil.copy2(target / 'version.json', root / 'version.json')
    scripts = root / 'sources/$OEM$/$$/Setup/Scripts'
    reject_symlink_path(scripts, root)
    scripts.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.wis-scripts-', dir=scripts.parent) as temp:
        stage = Path(temp)
        for f in (root / 'media/oem/Setup/Scripts').iterdir():
            shutil.copy2(f, stage / f.name)
        promote_directory(stage, scripts, backup_dir, 'scripts')
    wifi_root = root / 'Wifi-Servis.ini'
    if wifi_config:
        wifi_root.write_bytes(wifi_data)
        wifi_root.chmod(0o600)
        (target / 'Wifi-Servis.ini').chmod(0o600)
    elif wifi_root.exists():
        os.rename(wifi_root, backup_dir / ('wifi-' + uuid.uuid4().hex + '.ini'))
    files = {p.relative_to(root).as_posix(): sha256(p) for p in target.rglob('*')
             if p.is_file() and p.name != 'Wifi-Servis.ini'}
    files.update({p.relative_to(root).as_posix(): sha256(p) for p in scripts.iterdir()
                  if p.is_file() and p.suffix in ('.ps1', '.cmd', '.bat')})
    files['WIS.Launcher.exe'] = sha256(root / 'WIS.Launcher.exe')
    attestation = {'source_commit': commit, 'release_id': meta['release_id'],
                   'release_dir': str(release_dir.resolve()), 'wifi_config': bool(wifi_config), 'files': files}
    (root / '.wis-media-attestation.json').write_text(json.dumps(attestation, indent=2) + '\n')
    return verify_media(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release-dir', type=Path)
    parser.add_argument('--wifi-config', type=Path, help='Lokalan servisni config; nikada se ne potpisuje kao payload.')
    parser.add_argument('--verify-only', action='store_true')
    args = parser.parse_args()
    if args.verify_only:
        info = verify_media(ROOT)
    elif args.release_dir:
        info = prepare_media(ROOT, args.release_dir, args.wifi_config)
    else:
        parser.error('--release-dir je obavezan za pripremu.')
    print('[PASS] OEM medijum vezan za ' + info['source_commit'])


if __name__ == '__main__':
    try:
        main()
    except Exception as e:
        print('[FAIL] ' + str(e), file=sys.stderr)
        sys.exit(1)
