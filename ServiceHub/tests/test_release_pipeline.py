"""Build potvrda i OEM priprema: stvarne datoteke, Git commit i potpisani paket."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from servishub.releases import ReleaseError, publish_release, sha256
from tests.release_fixture import make_release, update_receipt

ROOT = Path(__file__).resolve().parents[2]


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'ISO' / 'Skripte' / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class ReleasePipelineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.workspace = Path(self.temp.name)
        self.root = self.workspace / 'ISO'
        self.root.mkdir()
        self.cache, self.key, self.public, self.bundle, _ = make_release(self.root)
        public_dest = self.workspace / 'Launcher/trusted_public_key.xml'
        public_dest.parent.mkdir(parents=True)
        shutil.copy2(self.public, public_dest)
        templates = self.root / 'media/oem/Setup/Scripts'
        templates.mkdir(parents=True)
        (templates / 'SetupComplete.cmd').write_text('@echo off\n')
        (self.bundle / 'WIS.Launcher.exe').write_bytes(b'fixture launcher')
        (self.root / '.gitignore').write_text('cache/\nbundle/\n*.pem\ntest-public.xml\nartifacts/\nsources/\n.wis-media-attestation.json\nWIS.Launcher.exe\n')
        subprocess.run(['git', 'init', '-q', str(self.root)], check=True)
        for k, v in [('user.name', 'Test'), ('user.email', 'test@localhost')]:
            subprocess.run(['git', '-C', str(self.root), 'config', k, v], check=True)
        subprocess.run(['git', '-C', str(self.root), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(self.root), 'commit', '-qm', 'fixture'], check=True)
        self.commit = subprocess.check_output(['git', '-C', str(self.root), 'rev-parse', 'HEAD'], text=True).strip()
        update_receipt(self.bundle, self.commit, '2.17.0')
        self.meta = publish_release(self.bundle, self.cache, '2.17.0', self.commit, self.key, self.public)
        self.release_dir = self.cache / 'releases' / self.meta['release_id']
        self.media = load_script('Prepare-WISMedia')
        self.release = load_script('Release-WIS')

    def tearDown(self):
        self.temp.cleanup()

    def test_oem_preparation_and_modified_component_gate(self):
        self.media.prepare_media(self.root, self.release_dir)
        self.assertEqual(self.media.verify_media(self.root)['source_commit'], self.commit)
        ui = self.root / 'sources/$OEM$/$1/setup/Skripte/WIS.UI/WIS.UI.exe'
        ui.write_bytes(b'modified')
        with self.assertRaises(ReleaseError):
            self.media.verify_media(self.root)

    def test_unsigned_extra_file_and_forged_attestation_rejected(self):
        self.media.prepare_media(self.root, self.release_dir)
        target = self.root / 'sources/$OEM$/$1/setup'
        (target / 'extra.exe').write_bytes(b'unsigned')
        marker = self.root / '.wis-media-attestation.json'
        info = json.loads(marker.read_text())
        info['files'] = {}  # Marker nije autoritet za heševe: koristi potpisani manifest.
        marker.write_text(json.dumps(info))
        with self.assertRaises(ReleaseError):
            self.media.verify_media(self.root)

    def test_new_commit_or_dirty_source_blocks_media(self):
        self.media.prepare_media(self.root, self.release_dir)
        (self.root / 'new-source.txt').write_text('new source')
        with self.assertRaises(ReleaseError):
            self.media.verify_media(self.root)

    def test_old_setup_script_and_secret_are_not_carried_forward(self):
        scripts = self.root / 'sources/$OEM$/$$/Setup/Scripts'
        scripts.mkdir(parents=True)
        (scripts / 'old.ps1').write_text('old code')
        (scripts / 'Wifi-Servis.ini').write_text('fixture only')
        self.media.prepare_media(self.root, self.release_dir)
        self.assertFalse((scripts / 'old.ps1').exists())
        self.assertFalse((scripts / 'Wifi-Servis.ini').exists())
        (scripts / 'new-unsigned.ps1').write_text('unexpected')
        with self.assertRaises(ReleaseError):
            self.media.verify_media(self.root)

    def test_symlink_media_parent_rejected_before_writing(self):
        outside = self.root / 'artifacts/outside'
        outside.mkdir(parents=True)
        (self.root / 'sources').symlink_to(outside, target_is_directory=True)
        with self.assertRaises(ReleaseError):
            self.media.prepare_media(self.root, self.release_dir)
        self.assertFalse((outside / '$OEM$').exists())

    def test_wrong_windows_receipt_rejected_before_packaging(self):
        build = self.root / 'artifacts/windows'
        build.mkdir(parents=True)
        receipt = build / 'build-attestation.json'
        receipt.write_text(json.dumps({'source_commit': 'b'*40, 'windows_powershell': '5.1', 'validation': 'passed'}))
        with self.assertRaises(ReleaseError):
            self.release.prepare_bundle(build, self.bundle, self.commit)
        receipt.write_text(json.dumps({'source_commit': self.commit, 'windows_powershell': '7.6', 'validation': 'passed'}))
        with self.assertRaises(ReleaseError):
            self.release.prepare_bundle(build, self.bundle, self.commit)

    def test_missing_or_changed_build_binary_rejected(self):
        build = self.root / 'artifacts/windows'
        build.mkdir(parents=True)
        binary = build / 'WIS.UI.exe'
        binary.write_bytes(b'compiled fixture')
        (build / 'build-attestation.json').write_text(json.dumps({
            'source_commit': self.commit, 'windows_powershell': '5.1',
            'validation': 'passed', 'files': {'WIS.UI.exe': '0'*64}}))
        with self.assertRaises(ReleaseError):
            self.release.prepare_bundle(build, self.bundle, self.commit)

    def test_four_subsystem_sources_preserve_installed_payload_layout(self):
        build = self.root / 'artifacts/windows'
        build.mkdir(parents=True)
        hashes = {}
        for name in ('WIS.UI.exe', 'WIS.Launcher.exe', 'HidSharp.dll', 'LibreHardwareMonitorLib.dll'):
            binary = build / name
            binary.write_bytes(('built ' + name).encode())
            hashes[name] = sha256(binary)
        (build / 'build-attestation.json').write_text(json.dumps({
            'source_commit': self.commit, 'windows_powershell': '5.1',
            'validation': 'passed', 'files': hashes}))
        for folder, name in [('Modules', 'WIS.Test.psm1'), ('Phases', 'Phase1-Test.ps1')]:
            source = self.workspace / 'UI/Skripte' / folder / name
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_text('# fixture runtime code')
        for name in ('FirstLogon.cmd', 'FirstLogon.ps1', 'SetupComplete.cmd', 'SetupComplete.ps1', 'Provera_autopilota.ps1'):
            (self.root / name).write_text('# fixture bootstrap')
        (self.workspace / 'version.json').write_text('{"version":"2.17.0"}')
        resources = self.root / 'resources/network-drivers.json'
        resources.parent.mkdir()
        resources.write_text('{"source":"Drivers/Net","files":{}}')
        output = self.root / 'assembled'
        output.mkdir()
        with patch.object(self.release, 'ROOT', self.workspace), patch.object(self.release, 'ISO_ROOT', self.root):
            self.release.prepare_bundle(build, output, self.commit)
        for path in ['WIS.Launcher.exe', 'Skripte/WIS.UI/WIS.UI.exe',
                     'Skripte/Modules/WIS.Test.psm1', 'Skripte/Phases/Phase1-Test.ps1',
                     'FirstLogon.cmd', 'SetupComplete.ps1', 'Provera_autopilota.ps1',
                     'Skripte/Provera_autopilota.ps1', 'version.json']:
            self.assertTrue((output / path).is_file(), path)
        self.assertFalse((output / 'UI').exists())
        self.assertFalse((output / 'ISO').exists())
