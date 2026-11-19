"""Scenario testovi stvarnog RSA/ZIP publishera i atomic pointera."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from servishub.payload import PayloadManager
from servishub.releases import ReleaseError, publish_release, verify_release
from tests.release_fixture import make_release, update_receipt

class TestPayloadManager(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.cache, self.key, self.public, self.bundle, self.meta = make_release(self.base)
        self.pm = PayloadManager(self.base, self.cache, self.bundle, self.public)
    def tearDown(self):
        self.temp.cleanup()
    def test_source_changes_never_rebuild(self):
        meta, path = self.pm.get_payload_file()
        before = Path(path).read_bytes()
        before_meta = Path(path).with_name('payload_meta.json').read_bytes()
        (self.bundle / 'new.ps1').write_text('changed source')
        self.assertEqual(self.pm.get_metadata(), meta)
        self.assertEqual(Path(path).read_bytes(), before)
        self.assertEqual(Path(path).with_name('payload_meta.json').read_bytes(), before_meta)
        with self.assertRaises(ReleaseError): self.pm.ensure_payload(force=True)
    def test_corrupt_zip_rejected_without_regeneration(self):
        _, path = self.pm.get_payload_file()
        with open(path, 'ab') as f: f.write(b'corrupt')
        with self.assertRaises(ReleaseError): self.pm.get_payload_file()
        self.assertTrue(Path(path).read_bytes().endswith(b'corrupt'))
    def test_bad_signature_and_unsigned_manifest(self):
        _, path = self.pm.get_payload_file()
        manifest = Path(path).with_name('payload_meta.json')
        bad = dict(self.meta, signature='AAAA')
        manifest.write_text(json.dumps(bad))
        with self.assertRaises(ReleaseError): self.pm.get_metadata()
        bad.pop('signature')
        manifest.write_text(json.dumps(bad))
        with self.assertRaises(ReleaseError): self.pm.get_metadata()
    def test_source_commit_bound_to_signature(self):
        _, path = self.pm.get_payload_file()
        Path(path).with_name('payload_meta.json').write_text(json.dumps(dict(self.meta, source_commit='b'*40)))
        with self.assertRaises(ReleaseError): self.pm.get_metadata()
    def test_sign_failure_preserves_current(self):
        pointer = (self.cache/'current.json').read_bytes()
        (self.bundle/'new.ps1').write_text('new version')
        update_receipt(self.bundle, 'b'*40, '2.17.1')
        with patch('servishub.releases.subprocess.run', side_effect=subprocess.CalledProcessError(1, 'openssl')):
            with self.assertRaises(subprocess.CalledProcessError):
                publish_release(self.bundle, self.cache, '2.17.1', 'b'*40, self.key, self.public)
        self.assertEqual((self.cache/'current.json').read_bytes(), pointer)
        self.assertEqual(self.pm.get_metadata(), self.meta)
    def test_pointer_interruption_preserves_old_download(self):
        meta, old_path = self.pm.get_payload_file()
        pointer = (self.cache/'current.json').read_bytes()
        (self.bundle/'new.ps1').write_text('new version')
        update_receipt(self.bundle, 'b'*40, '2.17.1')
        with patch('servishub.releases.os.replace', side_effect=OSError('interruption')):
            with self.assertRaises(OSError):
                publish_release(self.bundle, self.cache, '2.17.1', 'b'*40, self.key, self.public)
        self.assertEqual((self.cache/'current.json').read_bytes(), pointer)
        self.assertEqual(verify_release(old_path, Path(old_path).with_name('payload_meta.json'), self.public), meta)
    def test_activation_preserves_previous_release(self):
        _, old_path = self.pm.get_payload_file()
        (self.bundle/'new.ps1').write_text('new version')
        update_receipt(self.bundle, 'b'*40, '2.17.1')
        new = publish_release(self.bundle, self.cache, '2.17.1', 'b'*40, self.key, self.public)
        self.assertEqual(self.pm.get_metadata(), new)
        self.assertTrue(Path(old_path).is_file())
        self.assertNotEqual(self.pm.get_payload_file()[1], old_path)
    def test_idempotency_and_pointer_traversal(self):
        self.assertEqual(publish_release(self.bundle, self.cache, '2.17.0', 'a'*40, self.key, self.public), self.meta)
        (self.cache/'current.json').write_text('{"release_id":"../../outside"}')
        with self.assertRaises(ReleaseError): self.pm.get_metadata()
    def test_secret_and_symlink_rejected(self):
        secret = self.bundle/'Wifi-Servis.ini'; secret.write_text('fixture')
        with self.assertRaises(ReleaseError): publish_release(self.bundle, self.cache, '2.17.0', 'a'*40, self.key, self.public)
        secret.unlink(); (self.bundle/'symlink').symlink_to(self.public)
        with self.assertRaises(ReleaseError): publish_release(self.bundle, self.cache, '2.17.0', 'a'*40, self.key, self.public)
    def test_metadata_copy_protects_verified_cache(self):
        meta = self.pm.get_metadata(); meta['required_files'].clear()
        self.assertTrue(self.pm.get_metadata()['required_files'])
    def test_missing_release_does_not_create_cache(self):
        missing = self.base/'missing'
        pm = PayloadManager(self.base, missing, self.bundle, self.public)
        with self.assertRaises(ReleaseError): pm.get_metadata()
        self.assertFalse(missing.exists())
