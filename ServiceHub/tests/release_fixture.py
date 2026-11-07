"""Privremeni RSA ključ i potpisani paket; bez servisnih tajni."""
import base64
import hashlib
import json
from pathlib import Path
import subprocess
from servishub.releases import NEW_REQUIRED_COMPONENTS, publish_release


def make_release(base, commit='a' * 40, version='2.17.0'):
    base = Path(base)
    key, public = base / 'test-private.pem', base / 'test-public.xml'
    if not key.exists():
        subprocess.run(['openssl', 'genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', str(key)], check=True, capture_output=True)
        key.chmod(0o600)
        modulus = subprocess.check_output(['openssl', 'rsa', '-in', str(key), '-noout', '-modulus'], stderr=subprocess.DEVNULL).decode().strip().split('=', 1)[1]
        public.write_text('<RSAKeyValue><Modulus>%s</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>' % base64.b64encode(bytes.fromhex(modulus)).decode())
    bundle = base / 'bundle'
    bundle.mkdir(exist_ok=True)
    for name in NEW_REQUIRED_COMPONENTS:
        p = bundle / name
        p.parent.mkdir(parents=True, exist_ok=True)
        if not p.exists():
            p.write_bytes(('fixture: ' + name).encode())
    update_receipt(bundle, commit, version)
    cache = base / 'cache'
    meta = publish_release(bundle, cache, version, commit, key, public)
    return cache, key, public, bundle, meta


def update_receipt(bundle, commit, version):
    files = {}
    for name in ('WIS.UI.exe', 'WIS.Launcher.exe', 'HidSharp.dll', 'LibreHardwareMonitorLib.dll'):
        path = bundle / (name if name == 'WIS.Launcher.exe' else 'Skripte/WIS.UI/' + name)
        files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    (bundle / 'build-attestation.json').write_text(json.dumps(dict(
        source_commit=commit, version=version, windows_powershell='5.1', validation='passed', files=files)))
    (bundle / 'vm-validation.json').write_text(json.dumps(dict(
        source_commit=commit, status='PASS', restart_verified=True, anti_leak_verified=True)))
