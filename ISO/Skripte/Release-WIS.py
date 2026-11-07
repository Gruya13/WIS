#!/usr/bin/env python3
"""Eksplicitan WIS build → verifikacija → potpisivanje → atomarna lokalna objava."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ISO_ROOT = Path(__file__).resolve().parents[1]
ROOT = ISO_ROOT.parent
sys.path.insert(0, str(ROOT / 'ServiceHub'))
from servishub.releases import ReleaseError, publish_release, sha256, verify_release
from servishub.releases import safe_name


def git(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args], text=True).strip()


def prepare_bundle(build_dir, bundle, commit):
    receipt = json.loads((build_dir / 'build-attestation.json').read_text(encoding='utf-8-sig'))
    if receipt.get('source_commit') != commit or receipt.get('windows_powershell') != '5.1':
        raise ReleaseError('Build potvrda ne odgovara commit-u ili Windows PowerShell 5.1.')
    if receipt.get('validation') != 'passed':
        raise ReleaseError('Windows build/status/parse provere nisu prošle.')
    versions = json.loads((ROOT / 'version.json').read_text())
    if 'components' in versions:
        expected = {name: versions['components'][name]['version'] for name in ('iso', 'launcher', 'ui', 'servicehub')}
        if receipt.get('version') != versions['version'] or receipt.get('component_versions') != expected:
            raise ReleaseError('Build potvrda ne odgovara verziji WIS paketa i podsistema.')
    for name in ('WIS.UI.exe', 'WIS.Launcher.exe', 'HidSharp.dll', 'LibreHardwareMonitorLib.dll'):
        source = build_dir / name
        if receipt.get('files', {}).get(name) != sha256(source):
            raise ReleaseError('Build artefakt ne odgovara potvrdi: ' + name)
        dest = bundle / ('Skripte/WIS.UI/' + name if name in ('WIS.UI.exe', 'HidSharp.dll', 'LibreHardwareMonitorLib.dll') else name)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, dest)
    for folder, pattern in (('Skripte/Modules', '*'), ('Skripte/Phases', '*.ps1')):
        for source in sorted((ROOT / 'UI' / folder).glob(pattern)):
            if source.suffix not in ('.ps1', '.psm1'):
                continue
            dest = bundle / source.relative_to(ROOT / 'UI')
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, dest)
    for name in ('FirstLogon.cmd', 'FirstLogon.ps1', 'SetupComplete.cmd', 'SetupComplete.ps1', 'Provera_autopilota.ps1', 'version.json'):
        shutil.copy2((ROOT if name == 'version.json' else ISO_ROOT) / name, bundle / name)
    shutil.copy2(ISO_ROOT / 'Provera_autopilota.ps1', bundle / 'Skripte/Provera_autopilota.ps1')
    # Zadrži mrežni bootstrap na novom medijumu; svaki resurs je zaključan u Git-u.
    resources = json.loads((ISO_ROOT / 'resources/network-drivers.json').read_text())
    source_dir = ISO_ROOT / resources['source']
    for name, expected in resources['files'].items():
        source = source_dir / safe_name(name)
        if source.is_symlink() or sha256(source) != expected:
            raise ReleaseError('Mrežni resurs ne odgovara resource lock-u: ' + name)
        dest = bundle / 'Drivers/Net' / name
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, dest)
    # Build potvrda je deo potpisanog sadržaja. Servisne tajne nisu payload.
    shutil.copy2(build_dir / 'build-attestation.json', bundle / 'build-attestation.json')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    build = sub.add_parser('publish', help='Objavi u lokalnom cache-u; ne povezuje se sa NAS-om.')
    build.add_argument('--build-dir', type=Path, required=True)
    build.add_argument('--cache-dir', type=Path, required=True)
    build.add_argument('--private-key', type=Path, required=True)
    build.add_argument('--vm-report', type=Path, required=True,
                       help='PASS zapis namenske Windows VM za isti source commit.')
    verify = sub.add_parser('verify')
    verify.add_argument('--release-dir', type=Path, required=True)
    args = parser.parse_args()
    public_key = ROOT / 'Launcher/trusted_public_key.xml'
    if args.command == 'verify':
        meta = verify_release(args.release_dir / 'wis-payload.zip', args.release_dir / 'payload_meta.json', public_key)
    else:
        if git('status', '--porcelain'):
            raise ReleaseError('Objava zahteva čist Git commit; prvo sačuvajte izmene.')
        key = args.private_key.expanduser().resolve()
        if key.is_relative_to(ROOT):
            raise ReleaseError('Privatni ključ ne sme biti u repozitorijumu.')
        commit = git('rev-parse', 'HEAD')
        vm = json.loads(args.vm_report.read_text(encoding='utf-8-sig'))
        if vm.get('status') != 'PASS' or vm.get('source_commit') != commit or vm.get('restart_verified') is not True or vm.get('anti_leak_verified') is not True:
            raise ReleaseError('VM restart/cleanup provera nije potvrđena za ovaj commit.')
        versions = json.loads((ROOT / 'version.json').read_text())
        version = versions['version']
        with tempfile.TemporaryDirectory(prefix='wis-bundle-') as d:
            bundle = Path(d)
            prepare_bundle(args.build_dir.resolve(), bundle, commit)
            shutil.copy2(args.vm_report, bundle / 'vm-validation.json')
            meta = publish_release(bundle, args.cache_dir, version, commit, key, public_key,
                                   min_launcher_version=versions['compatibility']['min_launcher_version'])
    print(json.dumps({k: meta[k] for k in ('version', 'release_id', 'source_commit', 'sha256') if k in meta}, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (ReleaseError, OSError, ValueError, subprocess.CalledProcessError) as e:
        print('[FAIL] ' + str(e), file=sys.stderr)
        sys.exit(1)
