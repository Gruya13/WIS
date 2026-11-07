"""Stvarni loopback HTTP: validan paket, odsutno izdanje i korupcija."""
import hashlib
import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from servishub.handlers import ServisHubHandler
from servishub.payload import PayloadManager
from tests.release_fixture import make_release


class PayloadHttpTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        self.cache, _, self.public, self.bundle, self.meta = make_release(self.base)
        self.pm = PayloadManager(self.base, self.cache, self.bundle, self.public)
        handler = type('IsolatedPayloadHandler', (ServisHubHandler,), {'payload_mgr': self.pm})
        self.server = ThreadingHTTPServer(('127.0.0.1', 0), handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = 'http://127.0.0.1:%d' % self.server.server_port

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=3)
        self.temp.cleanup()

    def get(self, route):
        return urllib.request.urlopen(self.url + route, timeout=3)

    def assert_unavailable(self):
        for route in ('/api/payload/version', '/api/payload/download'):
            with self.assertRaises(urllib.error.HTTPError) as e:
                self.get(route)
            self.assertEqual(e.exception.code, 503)
            e.exception.close()

    def test_get_serves_actual_signed_release_without_source_rebuild(self):
        (self.bundle / 'new.ps1').write_text('source changed after publish')
        with self.get('/api/payload/version') as r:
            self.assertEqual(json.load(r), self.meta)
        with self.get('/api/payload/download') as r:
            data = r.read()
            self.assertIn('wis-payload.zip', r.headers['Content-Disposition'])
            self.assertEqual(len(data), int(r.headers['Content-Length']))
        self.assertEqual(hashlib.sha256(data).hexdigest(), self.meta['sha256'])

    def test_corruption_is_503_and_server_does_not_repair_or_publish(self):
        _, path = self.pm.get_payload_file()
        p = Path(path)
        before = p.with_name('payload_meta.json').read_bytes()
        p.write_bytes(b'broken')
        self.assert_unavailable()
        self.assertEqual(p.read_bytes(), b'broken')
        self.assertEqual(p.with_name('payload_meta.json').read_bytes(), before)

    def test_missing_release_is_503(self):
        (self.cache / 'current.json').unlink()
        self.assert_unavailable()
