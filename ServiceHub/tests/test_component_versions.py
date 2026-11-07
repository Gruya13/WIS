"""Independent subsystem versions remain bound to the signed release package."""
import json
import tempfile
import unittest
from servishub.releases import ReleaseError, publish_release
from tests.release_fixture import make_release


class ComponentVersionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.cache, self.key, self.public, self.bundle, _ = make_release(self.tmp.name)
        self.components = dict(iso='2.17.0', launcher='2.2.17', ui='2.1.0', servicehub='3.2.0')
        self.versions = dict(version='2.17.0', components={k: {'version': v} for k, v in self.components.items()},
                             compatibility={'min_launcher_version': '2.2.17'})
        self.receipt_path = self.bundle / 'build-attestation.json'
        self.receipt = json.loads(self.receipt_path.read_text())
        self.receipt['component_versions'] = self.components.copy()
        self.write_versions()

    def write_versions(self):
        (self.bundle / 'version.json').write_text(json.dumps(self.versions))
        self.receipt_path.write_text(json.dumps(self.receipt))

    def publish(self, minimum='2.2.17'):
        return publish_release(self.bundle, self.cache, '2.17.0', 'a'*40, self.key, self.public,
                               min_launcher_version=minimum)

    def test_independent_versions_are_signed_without_changing_package_version(self):
        meta = self.publish()
        self.assertEqual(meta['version'], '2.17.0')
        self.assertEqual(meta['min_launcher_version'], '2.2.17')
        self.assertIn('version.json', meta['required_files'])

    def test_mismatched_receipt_versions_rejected(self):
        self.receipt['component_versions']['ui'] = '2.1.1'
        self.write_versions()
        with self.assertRaises(ReleaseError):
            self.publish()

    def test_component_receipt_requires_signed_version_file(self):
        (self.bundle / 'version.json').unlink()
        with self.assertRaises(ReleaseError):
            self.publish()

    def test_manifest_minimum_must_match_signed_versions(self):
        with self.assertRaises(ReleaseError):
            self.publish('2.17.0')

    def test_missing_receipt_component_versions_rejected(self):
        del self.receipt['component_versions']
        self.write_versions()
        with self.assertRaises(ReleaseError):
            self.publish()

    def test_package_version_mismatch_rejected(self):
        self.versions['version'] = '2.18.0'
        self.write_versions()
        with self.assertRaises(ReleaseError):
            self.publish()
