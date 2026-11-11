"""Independent source versions are generated from a single canonical record."""
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('wis_versions', ROOT / 'ISO/Skripte/Sync-Versions.py')
versions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(versions)
class VersionSourceTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(); self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        for rel in ['version.json', 'Launcher/PayloadManager.cs', 'Launcher/Properties/AssemblyInfo.cs', 'UI/Properties/AssemblyInfo.cs']:
            p = self.root / rel; p.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(ROOT / rel, p)
        versions.sync_versions(self.root)
    def change(self, mutate):
        p = self.root / 'version.json'; data = json.loads(p.read_text()); mutate(data); p.write_text(json.dumps(data))
    def test_ui_update_does_not_change_launcher(self):
        expected_release = json.loads((self.root / 'version.json').read_text())['release_package']['version']
        path = self.root / 'Launcher/Properties/AssemblyInfo.cs'; before = path.read_bytes()
        self.change(lambda d: d['components']['ui'].update(version='2.1.1'))
        drift = versions.sync_versions(self.root, check=True)
        self.assertIn('UI/Properties/AssemblyInfo.cs', drift); self.assertNotIn('Launcher/Properties/AssemblyInfo.cs', drift)
        versions.sync_versions(self.root)
        self.assertEqual(versions.sync_versions(self.root, check=True), [])
        self.assertEqual(before, path.read_bytes())
        data = json.loads((self.root / 'UI/version.json').read_text())
        self.assertEqual(data['version'], '2.1.1'); self.assertEqual(data['wis_release_version'], expected_release)
    def test_incompatible_minimum_rejected(self):
        self.change(lambda d: d['compatibility'].update(min_launcher_version='2.2.18'))
        with self.assertRaises(ValueError): versions.sync_versions(self.root)
    def test_wrong_embedded_launcher_rejected(self):
        self.change(lambda d: d['components']['iso'].update(embedded_launcher_version='2.2.16'))
        with self.assertRaises(ValueError): versions.sync_versions(self.root)
