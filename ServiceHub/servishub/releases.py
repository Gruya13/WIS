"""Eksplicitno potpisivanje/objava i read-only provera WIS izdanja.

Privatni ključ koristi samo build proces. HTTP server ima samo javni ključ.
ZIP i metadata se objavljuju zajedno u nepromenljivom direktorijumu; current.json
se menja poslednji, jednim atomic replace. GET nikada ne pravi novi paket.
"""
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

MAX_ZIP_BYTES = 256 * 1024 * 1024
MAX_UNPACKED_BYTES = 512 * 1024 * 1024
REQUIRED_COMPONENTS = (
    'Skripte/WIS.UI/WIS.UI.exe',
    *('Skripte/Phases/Phase%d-%s.ps1' % (n, name) for n, name in enumerate(
        ('Network', 'Drivers', 'Optimization', 'Apps', 'Stress', 'Finalize'), 1)),
    *('Skripte/Modules/WIS.%s.psm1' % name for name in (
        'Common', 'Hardware', 'Network', 'Cleanup', 'Telemetry')),
    'Skripte/Modules/WIS.ScreenCapture.ps1',
)
NEW_REQUIRED_COMPONENTS = REQUIRED_COMPONENTS + (
    'Skripte/Modules/WIS.StatusContract.psm1', 'WIS.Launcher.exe',
    'Skripte/WIS.UI/HidSharp.dll', 'Skripte/WIS.UI/LibreHardwareMonitorLib.dll',
    'build-attestation.json', 'vm-validation.json',
)


class ReleaseError(ValueError):
    pass


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def safe_name(name):
    if not isinstance(name, str) or not name or '\\' in name or ':' in name or any(ord(c) < 32 or c in '<>"|?*' for c in name):
        raise ReleaseError('Neispravna putanja u paketu.')
    p = PurePosixPath(name)
    if p.is_absolute() or any(x in ('', '.', '..') for x in name.split('/')):
        raise ReleaseError('Putanja izlazi iz paketa.')
    if any(x.rstrip(' .') != x or x.split('.')[0].upper() in
           {'CON', 'PRN', 'AUX', 'NUL', *('COM%d' % n for n in range(1, 10)),
            *('LPT%d' % n for n in range(1, 10))} for x in p.parts):
        raise ReleaseError('Neispravna Windows putanja.')
    return name


def forbidden(name, data=b''):
    n = name.lower()
    return (n.endswith(('.pem', '.key', '.pfx')) or 'signing_private' in n or
            n.endswith('wifi-servis.ini') or 'wlan' in n and n.endswith('.xml') or
            n.endswith('.xml') and b'<keyMaterial>' in data or
            re.search(rb'(?m)^-----BEGIN (?:RSA |EC )?PRIVATE KEY-----\r?$', data) is not None)


def canonical(meta):
    pairs = '\n'.join('%s:%s' % (k, v.lower())
                      for k, v in sorted(meta['required_files'].items()))
    files_hash = hashlib.sha256(pairs.encode('utf-8')).hexdigest()
    return 'WIS|v2|%s|%s|%d|%s|%s|%s' % (
        meta['version'], meta['release_id'], meta['size'], meta['sha256'].lower(),
        meta['min_launcher_version'], files_hash)


def _der(tag, data):
    n = len(data)
    length = bytes([n]) if n < 128 else bytes([128 + (n.bit_length() + 7) // 8]) + n.to_bytes((n.bit_length() + 7) // 8, 'big')
    return bytes([tag]) + length + data


def public_pem(xml_path):
    root = ET.parse(xml_path).getroot()
    if root.tag != 'RSAKeyValue':
        raise ReleaseError('Neispravan javni ključ.')
    def integer(tag):
        value = base64.b64decode(root.findtext(tag), validate=True).lstrip(b'\0')
        if not value:
            raise ReleaseError('Prazan RSA parametar.')
        if tag == 'Modulus' and len(value) < 256:
            raise ReleaseError('Potreban je najmanje RSA-2048 ključ.')
        return _der(2, (b'\0' if value[0] & 128 else b'') + value)
    value = base64.b64encode(_der(48, integer('Modulus') + integer('Exponent'))).decode()
    return ('-----BEGIN RSA PUBLIC KEY-----\n' + '\n'.join(value[i:i+64] for i in range(0, len(value), 64)) + '\n-----END RSA PUBLIC KEY-----\n').encode()


def verify_release(zip_path, meta_path, public_key):
    """Proverava stvarni potpis, ZIP i sve deklarisane komponente; ništa ne piše."""
    zip_path, meta_path = Path(zip_path), Path(meta_path)
    if not zip_path.is_file() or not meta_path.is_file():
        raise ReleaseError('Nema objavljenog potpisanog izdanja.')
    if zip_path.stat().st_size > MAX_ZIP_BYTES or meta_path.stat().st_size > 1024 * 1024:
        raise ReleaseError('Paket ili manifest prelaze dozvoljenu veličinu.')
    meta = json.loads(meta_path.read_text(encoding='utf-8'))
    if meta.get('product') != 'WIS' or meta.get('schema_version') != 2:
        raise ReleaseError('Potreban je WIS manifest šeme 2.')
    for k in ('version', 'release_id', 'min_launcher_version'):
        if not isinstance(meta.get(k), str) or not meta[k] or '|' in meta[k] or '\n' in meta[k]:
            raise ReleaseError('Neispravno polje manifesta: ' + k)
    if not re.fullmatch(r'\d+\.\d+\.\d+', meta['version']):
        raise ReleaseError('Potreban je SemVer broj izdanja.')
    new_release = tuple(map(int, meta['version'].split('.'))) >= (2, 17, 0)
    if type(meta.get('size')) is not int or meta['size'] <= 0:
        raise ReleaseError('Neispravna veličina manifesta.')
    if meta.get('size_bytes', meta['size']) != meta['size'] or zip_path.stat().st_size != meta['size']:
        raise ReleaseError('Veličina ZIP-a ne odgovara manifestu.')
    if not re.fullmatch('[0-9a-f]{64}', str(meta.get('sha256', ''))):
        raise ReleaseError('Neispravan SHA-256.')
    files = meta.get('required_files')
    if not isinstance(files, dict) or not files:
        raise ReleaseError('Nedostaju heševi komponenti.')
    for k, h in files.items():
        safe_name(k)
        if not isinstance(h, str) or not re.fullmatch('[0-9a-f]{64}', h):
            raise ReleaseError('Neispravan hash komponente.')
    if any(k not in files for k in (NEW_REQUIRED_COMPONENTS if new_release else REQUIRED_COMPONENTS)):
        raise ReleaseError('Manifest ne pokriva obavezne WIS komponente.')
    expected = canonical(meta)
    if meta.get('signed_payload') != expected:
        raise ReleaseError('Potpisani sadržaj ne odgovara manifestu.')
    try:
        signature = base64.b64decode(meta['signature'], validate=True)
        with tempfile.TemporaryDirectory(prefix='wis-verify-') as d:
            d = Path(d)
            (d/'pub.pem').write_bytes(public_pem(public_key))
            (d/'sig').write_bytes(signature)
            (d/'data').write_bytes(expected.encode('utf-8'))
            proc = subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(d/'pub.pem'), '-signature', str(d/'sig'), str(d/'data')], capture_output=True, timeout=15)
            if proc.returncode != 0:
                raise ReleaseError('RSA-SHA256 potpis nije validan.')
    except (KeyError, TypeError, ET.ParseError) as e:
        raise ReleaseError('Neispravan potpis ili ključ.') from e
    if sha256(zip_path) != meta['sha256']:
        raise ReleaseError('SHA-256 ZIP-a ne odgovara manifestu.')
    hashes = {}
    receipt_data = None
    vm_data = None
    version_data = None
    names = set()
    with zipfile.ZipFile(zip_path) as z:
        infos = z.infolist()
        if sum(i.file_size for i in infos) > MAX_UNPACKED_BYTES or len(infos) > 4096:
            raise ReleaseError('Raspakovani paket je prevelik.')
        for info in infos:
            name = safe_name(info.filename.rstrip('/') if info.is_dir() else info.filename)
            if name.casefold() in names or stat.S_ISLNK(info.external_attr >> 16):
                raise ReleaseError('Dupla putanja ili symlink u ZIP-u.')
            names.add(name.casefold())
            if info.is_dir():
                continue
            data = z.read(info)
            if forbidden(name, data):
                raise ReleaseError('Paket sadrži servisnu tajnu ili privatni ključ.')
            hashes[name] = hashlib.sha256(data).hexdigest()
            if name == 'build-attestation.json':
                receipt_data = data
            if name == 'vm-validation.json':
                vm_data = data
            if name == 'version.json':
                version_data = data
    if any(hashes.get(k) != h for k, h in files.items()):
        raise ReleaseError('Komponente ZIP-a ne odgovaraju potpisanom manifestu.')
    if new_release or 'source_commit' in meta:
        commit = meta.get('source_commit')
        if not re.fullmatch('[0-9a-f]{40}', str(commit)) or not meta['release_id'].startswith(commit + '-'):
            raise ReleaseError('Release ID nije vezan za source commit.')
        if set(hashes) != set(files):
            raise ReleaseError('Novo izdanje mora potpisati svaku datoteku.')
        if new_release:
            receipt = json.loads(receipt_data.decode('utf-8-sig'))
            if receipt.get('source_commit') != commit or receipt.get('windows_powershell') != '5.1' or receipt.get('validation') != 'passed' or receipt.get('version') != meta['version']:
                raise ReleaseError('Build potvrda ne odgovara izdanju.')
            # Component versions are carried inside a hashed, signed file, keeping
            # the existing canonical manifest signature backward compatible.
            if version_data is not None or 'component_versions' in receipt:
                try:
                    versions = json.loads(version_data.decode('utf-8-sig')) if version_data is not None else {}
                    components = versions.get('components')
                    if components is not None or 'component_versions' in receipt:
                        if 'version.json' not in files or not isinstance(components, dict):
                            raise ReleaseError('Nedostaje potpisani zapis verzija podsistema.')
                        expected_versions = {name: components[name]['version'] for name in
                                             ('iso', 'launcher', 'ui', 'servicehub')}
                        if any(not isinstance(v, str) or not re.fullmatch(r'\d+\.\d+\.\d+', v)
                               for v in expected_versions.values()):
                            raise ReleaseError('Neispravne verzije podsistema.')
                        if (versions.get('version') != meta['version'] or
                                receipt.get('component_versions') != expected_versions or
                                versions.get('compatibility', {}).get('min_launcher_version') != meta['min_launcher_version']):
                            raise ReleaseError('Verzije podsistema ili kompatibilnost ne odgovaraju izdanju.')
                except (AttributeError, KeyError, TypeError, UnicodeError, json.JSONDecodeError) as exc:
                    raise ReleaseError('Neispravan zapis verzija podsistema.') from exc
            for name, path in [('WIS.UI.exe', 'Skripte/WIS.UI/WIS.UI.exe'), ('WIS.Launcher.exe', 'WIS.Launcher.exe'),
                               ('HidSharp.dll', 'Skripte/WIS.UI/HidSharp.dll'), ('LibreHardwareMonitorLib.dll', 'Skripte/WIS.UI/LibreHardwareMonitorLib.dll')]:
                if receipt.get('files', {}).get(name) != hashes.get(path):
                    raise ReleaseError('Build potvrda ne odgovara binarnoj komponenti.')
            vm = json.loads(vm_data.decode('utf-8-sig'))
            if vm.get('source_commit') != commit or vm.get('status') != 'PASS' or vm.get('restart_verified') is not True or vm.get('anti_leak_verified') is not True:
                raise ReleaseError('VM potvrda ne odgovara izdanju.')
    return meta


def publish_release(bundle_dir, cache_dir, version, source_commit, private_key, public_key, *, min_launcher_version="2.17.0"):
    """Jedini put za objavu. Ne menja postojeće izdanje ako bilo šta ne prođe."""
    bundle, cache = Path(bundle_dir).resolve(), Path(cache_dir).resolve()
    key = Path(private_key).resolve()
    if not re.fullmatch('[0-9a-f]{40}', source_commit) or not re.fullmatch(r'\d+\.\d+\.\d+', version) or not isinstance(min_launcher_version, str) or not re.fullmatch(r'\d+\.\d+\.\d+', min_launcher_version):
        raise ReleaseError('Potreban je pun Git commit i SemVer izdanje.')
    if not key.is_file() or key.stat().st_mode & 0o077:
        raise ReleaseError('Privatni ključ mora postojati i imati dozvole 0600.')
    if key.is_relative_to(bundle) or key.is_relative_to(cache):
        raise ReleaseError('Privatni ključ mora biti izvan paketa i skladišta.')
    releases = cache/'releases'
    releases.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.pending-', dir=releases) as tmp:
        stage = Path(tmp)
        hashes = {}
        with zipfile.ZipFile(stage/'wis-payload.zip', 'w', compression=zipfile.ZIP_DEFLATED) as z:
            for f in sorted(bundle.rglob('*')):
                if f.is_symlink():
                    raise ReleaseError('Symlink nije dozvoljen u paketu.')
                if not f.is_file():
                    continue
                name = safe_name(f.relative_to(bundle).as_posix())
                data = f.read_bytes()
                if forbidden(name, data):
                    raise ReleaseError('Paket sadrži tajnu: ' + name)
                hashes[name] = hashlib.sha256(data).hexdigest()
                info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o100644 << 16
                z.writestr(info, data)
        zip_hash = sha256(stage/'wis-payload.zip')
        release_id = source_commit + '-' + zip_hash[:16]
        size = (stage/'wis-payload.zip').stat().st_size
        meta = dict(product='WIS', schema_version=2, version=version, sem_version=version,
                    release_id=release_id, timestamp_version=release_id, source_commit=source_commit,
                    size=size, size_bytes=size, sha256=zip_hash, min_launcher_version=min_launcher_version,
                    required_files=hashes)
        meta['signed_payload'] = canonical(meta)
        (stage/'data').write_bytes(meta['signed_payload'].encode())
        sig = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(key), str(stage/'data')], capture_output=True, check=True, timeout=15).stdout
        meta['signature'] = base64.b64encode(sig).decode()
        (stage/'data').unlink()
        (stage/'payload_meta.json').write_text(json.dumps(meta, indent=2) + '\n')
        verify_release(stage/'wis-payload.zip', stage/'payload_meta.json', public_key)
        final = releases/release_id
        if final.exists():
            if verify_release(final/'wis-payload.zip', final/'payload_meta.json', public_key) != meta:
                raise ReleaseError('Release ID već postoji sa drugim sadržajem.')
        else:
            # TemporaryDirectory sme obrisati samo svoje staro ime, nikad final.
            os.rename(stage, final)
        with tempfile.NamedTemporaryFile('w', dir=cache, prefix='.current-', delete=False) as f:
            pointer = Path(f.name)
            json.dump({'release_id': release_id}, f)
            f.flush()
            os.fsync(f.fileno())
        try:
            os.replace(pointer, cache/'current.json')
        finally:
            pointer.unlink(missing_ok=True)
        return meta
