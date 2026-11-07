"""Preview ne prolazi produkcioni gate; potpis i OEM hash nisu opcioni."""
import base64
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile
from servishub.releases import ReleaseError, canonical, sha256, verify_release
from tests.release_fixture import make_release
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('preview_media', ROOT / 'ISO/Skripte/Build-PreviewMedia.py')
preview = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preview)


class PreviewMediaTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.base = Path(self.tmp.name)
        _, self.key, self.public, bundle, _ = make_release(self.base)
        self.root = self.base / 'snapshot/ISO'
        self.root.mkdir(parents=True)
        self.release = self.root.parent / 'preview-release'
        self.release.mkdir()
        self.commit = 'a' * 40
        proof = dict(source_commit=self.commit, build_environment='wine', validation='compiled_only',
                     windows_powershell='not_run', files={name: sha256(bundle / rel) for name, rel in preview.BINARIES.items()})
        preview.write_json(bundle / 'build-attestation.json', proof)
        preview.write_json(bundle / 'vm-validation.json', dict(source_commit=self.commit, status='not_run', restart_verified=False, anti_leak_verified=False))
        preview.write_json(bundle / preview.MARKER, dict(source_commit=self.commit, channel='preview-test-only'))
        (bundle / 'WIS-PREVIEW-README.txt').write_text('PREVIEW TEST ONLY')
        scripts = bundle / 'PreviewSetupScripts'
        scripts.mkdir()
        (scripts / 'SetupComplete.cmd').write_text('@echo off\n')
        hashes = {}
        with zipfile.ZipFile(self.release / 'wis-payload.zip', 'w') as z:
            for f in bundle.rglob('*'):
                if f.is_file():
                    name = f.relative_to(bundle).as_posix()
                    hashes[name] = sha256(f)
                    z.write(f, name)
        zp = self.release / 'wis-payload.zip'
        meta = dict(product='WIS', schema_version=2, version='2.17.0', source_commit=self.commit,
                    release_id=self.commit + '-' + sha256(zp)[:16], size=zp.stat().st_size,
                    sha256=sha256(zp), min_launcher_version='2.2.17', required_files=hashes)
        meta['signed_payload'] = canonical(meta)
        sig = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(self.key)], input=canonical(meta).encode(), capture_output=True, check=True).stdout
        meta['signature'] = base64.b64encode(sig).decode()
        preview.write_json(self.release / 'payload_meta.json', meta)
        self.setup = self.root / 'sources/$OEM$/$1/setup'
        shutil.copytree(bundle, self.setup)
        shutil.copy2(self.release / 'payload_meta.json', self.setup / 'payload_meta.json')
        for name in ('WIS.Launcher.exe', preview.MARKER, 'WIS-PREVIEW-README.txt'):
            shutil.copy2(bundle / name, self.root / name)
        shutil.copytree(scripts, self.root / 'sources/$OEM$/$$/Setup/Scripts')
        preview.write_json(self.root / '.wis-preview-attestation.json', dict(source_commit=self.commit, channel='preview-test-only', setup_scripts={}))

    def tearDown(self):
        self.tmp.cleanup()

    def test_preview_valid_but_production_rejects(self):
        self.assertEqual(preview.verify_preview(self.root, self.public)['version'], '2.17.0')
        with self.assertRaises(ReleaseError):
            verify_release(self.release / 'wis-payload.zip', self.release / 'payload_meta.json', self.public)

    def test_setup_script_tamper_rejected(self):
        (self.root / 'sources/$OEM$/$$/Setup/Scripts/SetupComplete.cmd').write_text('tampered')
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)

    def test_zip_tamper_rejected(self):
        with open(self.release / 'wis-payload.zip', 'ab') as f:
            f.write(b'tamper')
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)

    def test_unsigned_addition_rejected(self):
        (self.setup / 'extra.exe').write_bytes(b'unsigned')
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)

    def test_symlink_rejected(self):
        f = self.setup / 'WIS.Launcher.exe'
        f.unlink()
        f.symlink_to(self.root / 'WIS.Launcher.exe')
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)

    def test_signature_tamper_rejected(self):
        path = self.release / 'payload_meta.json'
        meta = json.loads(path.read_text())
        signature = bytearray(base64.b64decode(meta['signature']))
        signature[0] ^= 1
        meta['signature'] = base64.b64encode(signature).decode()
        preview.write_json(path, meta)
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)

    def test_wifi_config_verified_outside_signed_zip(self):
        (self.root / 'Wifi-Servis.ini').write_text('[WiFi]\nSSID=Test\n')
        (self.setup / 'Wifi-Servis.ini').write_text('[WiFi]\nSSID=Test\n')
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)
        preview.write_json(self.root / '.wis-preview-attestation.json',
                           dict(source_commit=self.commit, channel='preview-test-only', wifi_config=True, setup_scripts={}))
        self.assertEqual(preview.verify_preview(self.root, self.public)['version'], '2.17.0')
        (self.setup / 'Wifi-Servis.ini').write_text('[WiFi]\nSSID=Mismatch\n')
        with self.assertRaises(ReleaseError):
            preview.verify_preview(self.root, self.public)

