#!/usr/bin/env python3
"""Izolovan TEST medijum: Wine dokaz, potpis svih fajlova, bez produkcione objave."""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ServiceHub'))
from servishub.releases import (ReleaseError, NEW_REQUIRED_COMPONENTS, canonical,
                               forbidden, public_pem, safe_name, sha256)
MARKER = 'WIS-PREVIEW-TEST-ONLY.json'
BINARIES = {'WIS.Launcher.exe': 'WIS.Launcher.exe',
            'WIS.UI.exe': 'Skripte/WIS.UI/WIS.UI.exe',
            'HidSharp.dll': 'Skripte/WIS.UI/HidSharp.dll',
            'LibreHardwareMonitorLib.dll': 'Skripte/WIS.UI/LibreHardwareMonitorLib.dll'}


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n')


def verify_preview(root, public_key=None):
    """Poseban verifier; produkcioni verify_release namerno odbija ove dokaze."""
    root = Path(root).resolve()
    release = root.parent / 'preview-release'
    meta = json.loads((release / 'payload_meta.json').read_text())
    zip_path = release / 'wis-payload.zip'
    if zip_path.is_symlink() or zip_path.stat().st_size > 256 * 1024 * 1024:
        raise ReleaseError('Neispravan preview ZIP.')
    if (meta.get('product') != 'WIS' or meta.get('schema_version') != 2 or
            meta.get('signed_payload') != canonical(meta) or
            meta.get('size') != zip_path.stat().st_size or sha256(zip_path) != meta.get('sha256')):
        raise ReleaseError('Preview metadata ili hash nisu validni.')
    public_key = public_key or root.parent / 'Launcher/trusted_public_key.xml'
    with tempfile.TemporaryDirectory(prefix='wis-preview-verify-') as d:
        d = Path(d)
        (d/'public.pem').write_bytes(public_pem(public_key))
        (d/'data').write_bytes(canonical(meta).encode())
        (d/'sig').write_bytes(base64.b64decode(meta['signature'], validate=True))
        p = subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(d/'public.pem'),
                            '-signature', str(d/'sig'), str(d/'data')], capture_output=True, timeout=15)
        if p.returncode:
            raise ReleaseError('Preview potpis nije validan.')
    files, contents = {}, {}
    with zipfile.ZipFile(zip_path) as z:
        infos = z.infolist()
        if len(infos) > 4096 or sum(i.file_size for i in infos) > 512 * 1024 * 1024:
            raise ReleaseError('Preview ZIP prelazi granice.')
        seen = set()
        for info in infos:
            name = safe_name(info.filename)
            if info.is_dir() or name.casefold() in seen or stat.S_ISLNK(info.external_attr >> 16):
                raise ReleaseError('Preview duplikat, direktorijum ili symlink.')
            seen.add(name.casefold())
            data = z.read(info)
            if forbidden(name, data):
                raise ReleaseError('Preview sadrži tajnu.')
            files[name] = hashlib.sha256(data).hexdigest()
            if name in ('build-attestation.json', 'vm-validation.json', MARKER):
                contents[name] = json.loads(data)
    if files != meta.get('required_files') or any(n not in files for n in (*NEW_REQUIRED_COMPONENTS, MARKER)):
        raise ReleaseError('Preview fajlovi ne odgovaraju manifestu.')
    commit = meta.get('source_commit')
    import re
    if not re.fullmatch('[0-9a-f]{40}', str(commit)) or not meta['release_id'].startswith(commit + '-'):
        raise ReleaseError('Preview nije vezan za snapshot commit.')
    proof, vm, marker = (contents[n] for n in ('build-attestation.json', 'vm-validation.json', MARKER))
    if (marker.get('channel') != 'preview-test-only' or marker.get('source_commit') != commit or
            proof.get('source_commit') != commit or proof.get('build_environment') != 'wine' or
            proof.get('validation') != 'compiled_only' or proof.get('windows_powershell') != 'not_run' or
            vm != {'source_commit': commit, 'status': 'not_run', 'restart_verified': False, 'anti_leak_verified': False}):
        raise ReleaseError('Preview dokazi ne opisuju stvarnu ograničenu validaciju.')
    for binary, name in BINARIES.items():
        if proof.get('files', {}).get(binary) != files.get(name):
            raise ReleaseError('Preview build dokaz ne odgovara binarcima.')
    setup = root / 'sources/$OEM$/$1/setup'
    expected = {str(setup / name): digest for name, digest in files.items()}
    expected[str(setup / 'payload_meta.json')] = sha256(release / 'payload_meta.json')
    expected[str(root / 'WIS.Launcher.exe')] = files['WIS.Launcher.exe']
    expected[str(root / MARKER)] = files[MARKER]
    expected[str(root / 'WIS-PREVIEW-README.txt')] = files['WIS-PREVIEW-README.txt']
    scripts = root / 'sources/$OEM$/$$/Setup/Scripts'
    att = json.loads((root / '.wis-preview-attestation.json').read_text())
    if att.get('source_commit') != commit or att.get('channel') != 'preview-test-only':
        raise ReleaseError('Nedostaje preview media dokaz.')
    for name, digest in files.items():
        if name.startswith('PreviewSetupScripts/'):
            expected[str(scripts / name.split('/', 1)[1])] = digest
    for base in (setup, scripts):
        for f in base.rglob('*'):
            if f.is_symlink():
                raise ReleaseError('Nepotpisani dodatak preview OEM stablu.')
            if f.is_file() and str(f) not in expected:
                if f == setup / 'Wifi-Servis.ini' and att.get('wifi_config'):
                    continue
                raise ReleaseError('Nepotpisani dodatak preview OEM stablu.')
    if not att.get('wifi_config') and ((root / 'Wifi-Servis.ini').exists() or (setup / 'Wifi-Servis.ini').exists()):
        raise ReleaseError('Stari Wi-Fi config nije deo pripremljenog preview medijuma.')
    if att.get('wifi_config'):
        if not (root / 'Wifi-Servis.ini').is_file() or not (setup / 'Wifi-Servis.ini').is_file():
            raise ReleaseError('Nedostaje najavljeni Wi-Fi config na preview medijumu.')
        if (root / 'Wifi-Servis.ini').is_symlink() or (setup / 'Wifi-Servis.ini').is_symlink():
            raise ReleaseError('Wi-Fi config ne sme biti symlink.')
        if sha256(root / 'Wifi-Servis.ini') != sha256(setup / 'Wifi-Servis.ini'):
            raise ReleaseError('Wi-Fi config korena i OEM kopije se ne poklapaju.')
    for name, digest in expected.items():
        p = Path(name)
        if p.is_symlink() or not p.is_file() or sha256(p) != digest:
            raise ReleaseError('Preview medijum izmenjen: ' + p.name)
    return meta


def prepare(build_dir, proof_path, output, private_key, wifi_config=None):
    build_dir, output, private_key = (Path(p).resolve() for p in (build_dir, output, private_key))
    wifi_data = Path(wifi_config).read_bytes() if wifi_config else None
    if wifi_data is not None and len(wifi_data) > 64 * 1024:
        raise ReleaseError('Servisni Wi-Fi config je prevelik.')
    if output.exists() or output.is_relative_to(ROOT) and not output.is_relative_to(ROOT / '.workspace/artifacts'):
        raise ReleaseError('Koristiti nov izolovan direktorijum artefakata.')
    if private_key.is_relative_to(ROOT) or private_key.stat().st_mode & 0o077:
        raise ReleaseError('Postojeći privatni ključ mora biti izvan Git-a sa dozvolama 0600.')
    proof = json.loads(Path(proof_path).read_text())
    if proof.get('build_environment') != 'wine' or proof.get('validation') != 'compiled_only':
        raise ReleaseError('Potreban je stvarni Wine compiled_only build dokaz.')
    for name in BINARIES:
        if (build_dir / name).is_symlink() or proof.get('files', {}).get(name) != sha256(build_dir / name):
            raise ReleaseError('Build dokaz ne odgovara izlazu: ' + name)
    output.mkdir(parents=True)
    # Snapshot prati stvarne trenutne izvore, bez pisanja u originalni Git.
    names = subprocess.check_output(['git', '-C', str(ROOT), 'ls-files', '--cached', '--others', '--exclude-standard', '-z']).decode().split('\0')
    names += ['ISO/Skripte/Build-PreviewMedia.py']
    names = sorted(set(names))
    manifest = {}
    for name in filter(None, names):
        if name.startswith(('.workspace/', '.git/', '.agents/', '.codex/')) or 'Programi' in Path(name).parts:
            continue
        source = ROOT / safe_name(name)
        if not source.exists():
            continue
        if source.is_symlink():
            raise ReleaseError('Source snapshot ne prihvata symlink: ' + name)
        data = source.read_bytes()
        if forbidden(name, data):
            raise ReleaseError('Source snapshot sadrži tajnu: ' + name)
        target = output / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        manifest[name] = hashlib.sha256(data).hexdigest()
    write_json(output / 'preview-source-manifest.json', manifest)
    subprocess.run(['git', 'init', '-q', str(output)], check=True)
    subprocess.run(['git', '-C', str(output), 'add', '-f', '.'], check=True)
    subprocess.run(['git', '-C', str(output), '-c', 'user.name=WIS Preview Builder', '-c',
                    'user.email=preview@localhost', 'commit', '--no-gpg-sign', '-qm', 'Izolovan WIS preview source snapshot'], check=True)
    commit = subprocess.check_output(['git', '-C', str(output), 'rev-parse', 'HEAD'], text=True).strip()
    media = output / 'ISO'
    # Linkuju/kopiraju se samo read-only medijski resursi; OEM se gradi isključivo iz novog paketa.
    spec = importlib.util.spec_from_file_location('wis_iso', ROOT / 'ISO/Skripte/Build-Iso.py')
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    for name in mod.INCLUDED_ROOT_ITEMS:
        if name in ('Programi.tar', 'Wifi-Servis.ini', 'WIS.Launcher.exe', 'version.json'):
            continue
        source = ROOT / 'ISO' / name
        if not source.exists():
            continue
        candidates = source.rglob('*') if source.is_dir() else (source,)
        for f in candidates:
            rel = f.relative_to(ROOT / 'ISO')
            if '$OEM$' in rel.parts or any(part.casefold() == 'programi' for part in rel.parts):
                continue
            if f.is_symlink():
                raise ReleaseError('Media symlink nije dozvoljen.')
            if not f.is_file():
                continue
            if forbidden(rel.as_posix(), f.read_bytes() if f.suffix.lower() in ('.xml', '.ini') else b''):
                raise ReleaseError('Media sadrži tajnu: ' + rel.as_posix())
            target = media / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            if not target.exists():
                try:
                    os.link(f, target)
                except OSError:
                    shutil.copy2(f, target)
    bundle = output / 'preview-bundle'
    bundle.mkdir()
    for source_folder, pattern in (('Modules', '*'), ('Phases', '*.ps1')):
        for f in (output / 'UI/Skripte' / source_folder).glob(pattern):
            if f.suffix not in ('.ps1', '.psm1'):
                continue
            target = bundle / 'Skripte' / source_folder / f.name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, target)
    for binary, rel in BINARIES.items():
        dest = bundle / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(build_dir / binary, dest)
    for name in ('FirstLogon.cmd', 'FirstLogon.ps1', 'SetupComplete.cmd', 'SetupComplete.ps1', 'Provera_autopilota.ps1'):
        shutil.copy2(media / name, bundle / name)
    shutil.copy2(media / 'Provera_autopilota.ps1', bundle / 'Skripte/Provera_autopilota.ps1')
    shutil.copy2(output / 'version.json', bundle / 'version.json')
    versions = json.loads((output / 'version.json').read_text())
    lock = json.loads((media / 'resources/network-drivers.json').read_text())
    for name, digest in lock['files'].items():
        source = ROOT / 'ISO' / lock['source'] / safe_name(name)
        if source.is_symlink() or sha256(source) != digest:
            raise ReleaseError('Mrežni driver ne odgovara lock-u.')
        target = bundle / 'Drivers/Net' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    proof.update(source_commit=commit, version=versions['version'], windows_powershell='not_run')
    write_json(bundle / 'build-attestation.json', proof)
    write_json(bundle / 'vm-validation.json', dict(source_commit=commit, status='not_run', restart_verified=False, anti_leak_verified=False))
    write_json(bundle / MARKER, dict(channel='preview-test-only', source_commit=commit,
               version=versions['version'], warning='TEST ISO: Windows PS5.1/VM/E2E nisu izvršeni. Nije produkciono izdanje.'))
    (bundle / 'WIS-PREVIEW-README.txt').write_text('WIS 2.17.0 PREVIEW - SAMO ZA TEST\nWindows PS5.1, VM i instalacioni E2E nisu potvrđeni.\nProgrami ostaju na NAS-u. Ako NAS nudi stariji paket (2.16.x), Launcher automatski zadržava verifikovani lokalni 2.17.0 paket.\nLokalni UI se pokreće tek posle potpune provere digitalnog potpisa i fajlova.\nNAS i produkcioni instalacioni mediji nisu menjani.\n', encoding='utf-8')
    shutil.copytree(media / 'media/oem/Setup/Scripts', bundle / 'PreviewSetupScripts')
    release = output / 'preview-release'
    release.mkdir()
    hashes = {}
    with zipfile.ZipFile(release / 'wis-payload.zip', 'w', zipfile.ZIP_DEFLATED) as z:
        for f in sorted(bundle.rglob('*')):
            if not f.is_file():
                continue
            name, data = safe_name(f.relative_to(bundle).as_posix()), f.read_bytes()
            if f.is_symlink() or forbidden(name, data):
                raise ReleaseError('Preview payload sadrži symlink/tajnu.')
            hashes[name] = hashlib.sha256(data).hexdigest()
            z.writestr(name, data)
    zip_path = release / 'wis-payload.zip'
    digest, size = sha256(zip_path), zip_path.stat().st_size
    meta = dict(product='WIS', schema_version=2, version=versions['version'],
                release_id=commit + '-' + digest[:16], source_commit=commit, size=size, size_bytes=size,
                sha256=digest, min_launcher_version=versions['compatibility']['min_launcher_version'], required_files=hashes)
    meta['signed_payload'] = canonical(meta)
    signature = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(private_key)],
                              input=canonical(meta).encode(), capture_output=True, check=True, timeout=15).stdout
    meta['signature'] = base64.b64encode(signature).decode()
    write_json(release / 'payload_meta.json', meta)
    setup = media / 'sources/$OEM$/$1/setup'
    shutil.copytree(bundle, setup)
    shutil.copy2(release / 'payload_meta.json', setup / 'payload_meta.json')
    shutil.copy2(bundle / 'WIS.Launcher.exe', media / 'WIS.Launcher.exe')
    shutil.copy2(bundle / 'version.json', media / 'version.json')
    shutil.copy2(bundle / MARKER, media / MARKER)
    shutil.copy2(bundle / 'WIS-PREVIEW-README.txt', media / 'WIS-PREVIEW-README.txt')
    scripts = media / 'sources/$OEM$/$$/Setup/Scripts'
    shutil.copytree(media / 'media/oem/Setup/Scripts', scripts)
    if wifi_data is not None:
        for wifi_dest in (media / 'Wifi-Servis.ini', setup / 'Wifi-Servis.ini'):
            wifi_dest.write_bytes(wifi_data)
            wifi_dest.chmod(0o600)
    write_json(media / '.wis-preview-attestation.json', dict(channel='preview-test-only', source_commit=commit,
               wifi_config=bool(wifi_config),
               setup_scripts={f.name: sha256(f) for f in scripts.iterdir() if f.is_file()}))
    verify_preview(media)
    print(json.dumps({'media_root': str(media), 'source_commit': commit, 'release_id': meta['release_id'],
                      'status': 'PREVIEW_ONLY', 'windows_vm': 'not_run'}, indent=2))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--build-dir', type=Path)
    p.add_argument('--build-proof', type=Path)
    p.add_argument('--output-dir', type=Path)
    p.add_argument('--private-key', type=Path)
    p.add_argument('--wifi-config', type=Path, help='Lokalan servisni Wi-Fi config izvan potpisanog ZIP-a i Git-a.')
    p.add_argument('--verify-only', type=Path, metavar='MEDIA_ROOT')
    args = p.parse_args()
    if args.verify_only:
        verify_preview(args.verify_only)
        print('[PASS] Potpisani TEST preview medijum; Windows/VM nisu potvrđeni.')
    elif all((args.build_dir, args.build_proof, args.output_dir, args.private_key)):
        prepare(args.build_dir, args.build_proof, args.output_dir, args.private_key, args.wifi_config)
    else:
        p.error('Potreban --verify-only ili sva četiri build argumenta.')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as e:
        print('[FAIL] ' + str(e), file=sys.stderr)
        sys.exit(1)
