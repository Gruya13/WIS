"""
tests.test_screen_mirror
========================
Kompleksni integracioni i jedinični testovi za 1 Hz Screen Mirror bafer i REST endpoint-e:
- In-memory thread-safe frame bafer u FleetStore (store_screen_frame, get_screen_frame).
- POST /api/devices/{device_id}/screen-frame (binarni JPEG prijem).
- GET /api/devices/{device_id}/screen-frame.jpg i alias /screen-frame.
- Provera isteka (TTL stale provera).
- Zaštita memorije (prazno telo, limit 10 MB, Content-Type validacija).
- Propagacija has_screen_stream u /api/devices.

Striktna No-Mock politika: koristi stvarne JPEG bajtove i realan ThreadingHTTPServer.
"""

import io
import os
import time
import json
import shutil
import tempfile
import threading
import unittest
import urllib.request
import urllib.error
from http.server import ThreadingHTTPServer
try:
    from PIL import Image
    HAS_PILLOW = True
except ImportError:
    HAS_PILLOW = False
    Image = None

from servishub.config import HubConfig
from servishub.telemetry import FleetStore
from servishub.handlers import ServisHubHandler


def create_sample_jpeg_bytes(width: int = 320, height: int = 240, color: str = "red") -> bytes:
    """Generiše stvarne JPEG binarne bajtove u memoriji."""
    if not HAS_PILLOW or Image is None:
        return b""
    img = Image.new("RGB", (width, height), color=color)
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=75)
    return buf.getvalue()


@unittest.skipUnless(HAS_PILLOW, "Pillow nije instaliran (opciona dev zavisnost za screen mirror)")
class TestScreenMirror(unittest.TestCase):
    """Integracioni testovi Screen Mirror sistema bez mock-ova."""

    @classmethod
    def setUpClass(cls):
        cls.test_dir = tempfile.mkdtemp()
        cls.config = HubConfig(
            host="127.0.0.1",
            port=0,
            base_dir=cls.test_dir,
            storage_dir=os.path.join(cls.test_dir, "storage"),
            logs_dir=os.path.join(cls.test_dir, "logs"),
        )
        cls.fleet_store = FleetStore(
            live_logs_dir=cls.config.live_logs_dir,
            reports_dir=cls.config.reports_dir,
        )

        ServisHubHandler.fleet_store = cls.fleet_store
        ServisHubHandler.config = cls.config

        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), ServisHubHandler)
        cls.port = cls.httpd.server_address[1]
        cls.base_url = f"http://127.0.0.1:{cls.port}"

        cls.server_thread = threading.Thread(target=cls.httpd.serve_forever, daemon=True)
        cls.server_thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.httpd.server_close()
        shutil.rmtree(cls.test_dir, ignore_errors=True)

    def test_01_fleet_store_unit_logic(self):
        """Testira direktne metode FleetStore-a za upravljanje frejmovima."""
        store = FleetStore()
        jpeg_bytes = create_sample_jpeg_bytes(100, 100, "green")

        # 1. Čuvanje frejma
        success = store.store_screen_frame("TEST-PC-01", jpeg_bytes)
        self.assertTrue(success)

        # 2. Dohvatanje svežeg frejma
        res = store.get_screen_frame("TEST-PC-01", max_age_seconds=5.0)
        self.assertIsNotNone(res)
        frame, ts = res
        self.assertEqual(frame, jpeg_bytes)
        self.assertAlmostEqual(ts, time.time(), delta=2.0)
        self.assertTrue(store.has_active_screen_stream("TEST-PC-01"))

        # 3. Nevalidni podaci
        self.assertFalse(store.store_screen_frame("", jpeg_bytes))
        self.assertFalse(store.store_screen_frame("TEST-PC-01", b""))
        self.assertIsNone(store.get_screen_frame("NON-EXISTENT"))

        # 4. Istekao frejm preko prošlog client_ts
        store.store_screen_frame("EXPIRED-PC", jpeg_bytes, client_ts=time.time() - 10.0)
        res_exp = store.get_screen_frame("EXPIRED-PC", max_age_seconds=5.0)
        self.assertIsNone(res_exp)
        self.assertFalse(store.has_active_screen_stream("EXPIRED-PC"))

        # 5. Sanitize ID konzistentnost
        store.store_screen_frame("KABINET 01", jpeg_bytes)
        res_san = store.get_screen_frame("KABINET_01")
        self.assertIsNotNone(res_san)
        self.assertEqual(res_san[0], jpeg_bytes)

    def test_02_post_and_get_screen_frame_http(self):
        """Testira POST i GET rute za prenos frejma preko HTTP-a."""
        device_id = "RADNA-STANICA-1"
        jpeg_bytes = create_sample_jpeg_bytes(640, 480, "blue")

        # POST /api/devices/{device_id}/screen-frame
        url_post = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req_post = urllib.request.Request(url_post, data=jpeg_bytes, method="POST")
        req_post.add_header("Content-Type", "image/jpeg")

        with urllib.request.urlopen(req_post, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            data = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(data.get("status"), "ok")
            self.assertEqual(data.get("device_id"), device_id)
            self.assertEqual(data.get("size_bytes"), len(jpeg_bytes))
            self.assertIn("timestamp", data)

        # GET /api/devices/{device_id}/screen-frame.jpg
        url_get = f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg"
        req_get = urllib.request.Request(url_get, method="GET")

        with urllib.request.urlopen(req_get, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.headers.get("Content-Type"), "image/jpeg")
            self.assertEqual(int(resp.headers.get("Content-Length")), len(jpeg_bytes))
            self.assertIn("no-cache", resp.headers.get("Cache-Control", ""))
            fetched_bytes = resp.read()
            # Provera binarnog integriteta
            self.assertEqual(fetched_bytes, jpeg_bytes)

    def test_03_get_alias_route(self):
        """Testira GET alias bez ekstenzije (.jpg)."""
        device_id = "RADNA-STANICA-ALIAS"
        jpeg_bytes = create_sample_jpeg_bytes(200, 200, "purple")

        # Store via POST
        url_post = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req_post = urllib.request.Request(url_post, data=jpeg_bytes, method="POST")
        req_post.add_header("Content-Type", "application/octet-stream")
        with urllib.request.urlopen(req_post, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # GET alias: /api/devices/{device_id}/screen-frame
        url_alias = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req_alias = urllib.request.Request(url_alias, method="GET")
        with urllib.request.urlopen(req_alias, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.headers.get("Content-Type"), "image/jpeg")
            self.assertEqual(resp.read(), jpeg_bytes)

    def test_04_url_decoding_device_id(self):
        """Testira podršku za URL-enkodovane nazive uređaja (razmaci i specijalni karakteri)."""
        raw_device_id = "SERVIS ŠALTER #1"
        encoded_device_id = urllib.parse.quote(raw_device_id)
        jpeg_bytes = create_sample_jpeg_bytes(160, 120, "orange")

        url_post = f"{self.base_url}/api/devices/{encoded_device_id}/screen-frame"
        req_post = urllib.request.Request(url_post, data=jpeg_bytes, method="POST")
        req_post.add_header("Content-Type", "image/jpeg")
        with urllib.request.urlopen(req_post, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            data = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(data.get("device_id"), raw_device_id)

        url_get = f"{self.base_url}/api/devices/{encoded_device_id}/screen-frame.jpg"
        req_get = urllib.request.Request(url_get, method="GET")
        with urllib.request.urlopen(req_get, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            self.assertEqual(resp.read(), jpeg_bytes)

    def test_05_client_timestamp_header(self):
        """Testira prosleđivanje X-Client-Timestamp hedera."""
        device_id = "DEV-WITH-TS"
        jpeg_bytes = create_sample_jpeg_bytes(100, 100, "yellow")
        custom_ts = 1710000123.456

        url_post = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req_post = urllib.request.Request(url_post, data=jpeg_bytes, method="POST")
        req_post.add_header("Content-Type", "image/jpeg")
        req_post.add_header("X-Client-Timestamp", str(custom_ts))

        with urllib.request.urlopen(req_post, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            data = json.loads(resp.read().decode("utf-8"))
            self.assertEqual(data.get("timestamp"), custom_ts)

    def test_06_stale_stream_ttl_expiry(self):
        """Testira istek strima ekrana (TTL stariji od 5 sekundi) i 404 odgovor."""
        device_id = "DEV-STALE-STREAM"
        jpeg_bytes = create_sample_jpeg_bytes(100, 100, "cyan")

        # Šaljemo frejm sa vremenskom oznakom od pre 15 sekundi
        old_ts = time.time() - 15.0
        url_post = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req_post = urllib.request.Request(url_post, data=jpeg_bytes, method="POST")
        req_post.add_header("Content-Type", "image/jpeg")
        req_post.add_header("X-Client-Timestamp", str(old_ts))
        with urllib.request.urlopen(req_post, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # GET za bajati frejm mora vratiti 404
        url_get = f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg"
        req_get = urllib.request.Request(url_get, method="GET")

        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_get, timeout=5)

        self.assertEqual(ctx.exception.code, 404)
        err_body = ctx.exception.read().decode("utf-8")
        err_json = json.loads(err_body)
        self.assertEqual(err_json.get("status"), "error")
        self.assertIn("stale", err_json.get("message", "").lower())

    def test_07_invalid_requests_handling(self):
        """Testira rukovanje nevalidnim zahtevima: nepodržan format, prazno telo, preveliko telo."""
        device_id = "DEV-INVALID"

        # 1. Pogrešan Content-Type (text/plain)
        url = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req = urllib.request.Request(url, data=b"Not a jpeg", method="POST")
        req.add_header("Content-Type", "text/plain")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req, timeout=5)
        self.assertEqual(ctx.exception.code, 400)

        # 2. Prazno telo zahteva (Content-Length: 0)
        req_empty = urllib.request.Request(url, data=b"", method="POST")
        req_empty.add_header("Content-Type", "image/jpeg")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_empty, timeout=5)
        self.assertEqual(ctx.exception.code, 400)

        # 3. Preveliko telo (> 10 MB)
        oversized_len = 11 * 1024 * 1024  # 11 MB
        req_huge = urllib.request.Request(url, data=b"x" * 100, method="POST")
        req_huge.add_header("Content-Type", "image/jpeg")
        req_huge.add_header("Content-Length", str(oversized_len))
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_huge, timeout=5)
        self.assertEqual(ctx.exception.code, 400)

        # 4. Nepostojeći uređaj GET
        url_nonexistent = f"{self.base_url}/api/devices/DOES-NOT-EXIST/screen-frame.jpg"
        req_nonexistent = urllib.request.Request(url_nonexistent, method="GET")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_nonexistent, timeout=5)
        self.assertEqual(ctx.exception.code, 404)

    def test_08_devices_summary_has_screen_stream(self):
        """Testira da /api/devices signalizira has_screen_stream kada je strim aktivan."""
        device_id = "STREAM-SUMMARY-PC"

        # Registruj uređaj u floti preko pulsa
        heartbeat_data = {
            "computer_name": device_id,
            "device_id": device_id,
            "status": "ACTIVE",
        }
        req_hb = urllib.request.Request(
            f"{self.base_url}/api/heartbeat",
            data=json.dumps(heartbeat_data).encode("utf-8"),
            method="POST",
        )
        req_hb.add_header("Content-Type", "application/json")
        with urllib.request.urlopen(req_hb, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # Provera pre slanja frejma: has_screen_stream mora biti False
        with urllib.request.urlopen(f"{self.base_url}/api/devices", timeout=5) as resp:
            devices = json.loads(resp.read().decode("utf-8"))
            dev_info = devices.get(device_id) or devices.get("stream_summary_pc")
            self.assertIsNotNone(dev_info)
            self.assertFalse(dev_info.get("has_screen_stream", False))

        # Pošalji frejm
        jpeg_bytes = create_sample_jpeg_bytes(100, 100, "black")
        req_frame = urllib.request.Request(
            f"{self.base_url}/api/devices/{device_id}/screen-frame",
            data=jpeg_bytes,
            method="POST",
        )
        req_frame.add_header("Content-Type", "image/jpeg")
        with urllib.request.urlopen(req_frame, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # Provera nakon slanja frejma: has_screen_stream mora biti True
        with urllib.request.urlopen(f"{self.base_url}/api/devices", timeout=5) as resp:
            devices = json.loads(resp.read().decode("utf-8"))
            dev_info = devices.get(device_id) or devices.get("stream_summary_pc")
            self.assertIsNotNone(dev_info)
            self.assertTrue(dev_info.get("has_screen_stream", False))

    def test_09_binary_integrity_and_magic_bytes(self):
        """
        Rigorozna provera binarnog integriteta JPEG frejma:
        - JPEG magic bytes (\\xff\\xd8\\xff na pocetku)
        - JPEG EOI marker (\\xff\\xd9 na kraju)
        - Tacna velicina i Content-Length heder
        - Validno dekodiranje i provera dimenzija kroz PIL.Image bez greske
        """
        device_id = "WORKER-INTEGRITY-01"
        target_w, target_h = 1280, 720
        jpeg_bytes = create_sample_jpeg_bytes(target_w, target_h, "magenta")

        # 1. Provera sirovih generisanih bajtova
        self.assertTrue(jpeg_bytes.startswith(b"\xff\xd8\xff"), "Generisani JPEG mora pocinjati sa magic bajtovima \\xff\\xd8\\xff")
        self.assertTrue(jpeg_bytes.endswith(b"\xff\xd9"), "Generisani JPEG mora se zavrsavati sa EOI markerom \\xff\\xd9")

        # 2. POST slanje na server
        url_post = f"{self.base_url}/api/devices/{device_id}/screen-frame"
        req_post = urllib.request.Request(url_post, data=jpeg_bytes, method="POST")
        req_post.add_header("Content-Type", "image/jpeg")

        with urllib.request.urlopen(req_post, timeout=5) as resp_post:
            self.assertEqual(resp_post.status, 200)
            data_post = json.loads(resp_post.read().decode("utf-8"))
            self.assertEqual(data_post.get("size_bytes"), len(jpeg_bytes))

        # 3. GET dohvatanje frejma i provera hedera i bajtova
        url_get = f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg"
        req_get = urllib.request.Request(url_get, method="GET")

        with urllib.request.urlopen(req_get, timeout=5) as resp_get:
            self.assertEqual(resp_get.status, 200)
            self.assertEqual(resp_get.headers.get("Content-Type"), "image/jpeg")
            self.assertEqual(int(resp_get.headers.get("Content-Length")), len(jpeg_bytes))
            self.assertIn("no-cache", resp_get.headers.get("Cache-Control", ""))

            fetched_bytes = resp_get.read()
            self.assertEqual(len(fetched_bytes), len(jpeg_bytes))
            self.assertEqual(fetched_bytes, jpeg_bytes)

            # Verifikacija magic bytes na primljenim bajtovima
            self.assertEqual(fetched_bytes[:3], b"\xff\xd8\xff")
            self.assertEqual(fetched_bytes[-2:], b"\xff\xd9")

            # Verifikacija dekompresije slike preko Pillow bez greske
            image_stream = io.BytesIO(fetched_bytes)
            with Image.open(image_stream) as img:
                self.assertEqual(img.format, "JPEG")
                self.assertEqual(img.size, (target_w, target_h))
                img.verify()

    def test_10_granular_ttl_and_query_parameter(self):
        """
        Rigorozna provera TTL ponasanja i grancnih slucajeva:
        - Frejm mladji od 5s vraca 200 OK
        - Frejm stariji od 5s vraca 404/stale
        - Custom ?max_age query parametar (npr. max_age=2.0s, max_age=10.0s)
        - Nevalidan max_age (fallback na 5.0s)
        """
        device_id = "DEV-TTL-GRANULAR"
        jpeg_bytes = create_sample_jpeg_bytes(200, 150, "navy")
        now = time.time()

        # 1. Slanje frejma starog 3.5 sekunde
        ts_3_5s_old = now - 3.5
        req_post = urllib.request.Request(
            f"{self.base_url}/api/devices/{device_id}/screen-frame",
            data=jpeg_bytes,
            headers={"Content-Type": "image/jpeg", "X-Client-Timestamp": str(ts_3_5s_old)},
            method="POST",
        )
        with urllib.request.urlopen(req_post, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # Standardni GET (podrazumevani max_age=5.0s) -> 3.5s < 5.0s => MORA biti 200
        req_get_default = urllib.request.Request(f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg")
        with urllib.request.urlopen(req_get_default, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # GET sa max_age=2.0s -> 3.5s > 2.0s => MORA biti 404 (stale)
        req_get_short_ttl = urllib.request.Request(f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg?max_age=2.0")
        with self.assertRaises(urllib.error.HTTPError) as ctx_short:
            urllib.request.urlopen(req_get_short_ttl, timeout=5)
        self.assertEqual(ctx_short.exception.code, 404)
        err_short = json.loads(ctx_short.exception.read().decode("utf-8"))
        self.assertEqual(err_short.get("status"), "error")
        self.assertIn("stale", err_short.get("message", "").lower())

        # GET sa max_age=10.0s -> 3.5s < 10.0s => MORA biti 200
        req_get_long_ttl = urllib.request.Request(f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg?max_age=10.0")
        with urllib.request.urlopen(req_get_long_ttl, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # GET sa nevalidnim max_age=abc -> fallback na 5.0s => MORA biti 200
        req_get_invalid_param = urllib.request.Request(f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg?max_age=abc")
        with urllib.request.urlopen(req_get_invalid_param, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # 2. Slanje frejma starog 5.5 sekundi
        ts_5_5s_old = now - 5.5
        req_post_stale = urllib.request.Request(
            f"{self.base_url}/api/devices/{device_id}/screen-frame",
            data=jpeg_bytes,
            headers={"Content-Type": "image/jpeg", "X-Client-Timestamp": str(ts_5_5s_old)},
            method="POST",
        )
        with urllib.request.urlopen(req_post_stale, timeout=5) as resp:
            self.assertEqual(resp.status, 200)

        # Standardni GET -> 5.5s > 5.0s => MORA biti 404
        with self.assertRaises(urllib.error.HTTPError) as ctx_stale:
            urllib.request.urlopen(req_get_default, timeout=5)
        self.assertEqual(ctx_stale.exception.code, 404)

    def test_11_concurrency_multi_client_and_dashboard_readers(self):
        """
        Provera ponasanja pri konkurentnom pristupu:
        - Vise klijenata istovremeno salje frejmove razlicitih uredjaja
        - Dashboard citaci istovremeno vuku frejmove i listu uredjaja
        - Thread-safety, odsustvo trka (race conditions) i deadlocks
        - 100% validnost podataka bez korupcije frejmova
        """
        num_devices = 5
        frames_per_device = 4
        client_devices = [f"CONC-WORKER-{i:02d}" for i in range(1, num_devices + 1)]
        shared_device = "CONC-SHARED-STREAM"

        errors = []
        stop_event = threading.Event()

        def client_worker(dev_id):
            try:
                for f_idx in range(frames_per_device):
                    if stop_event.is_set():
                        break
                    color = ["red", "blue", "green", "yellow", "cyan"][f_idx % 5]
                    frame = create_sample_jpeg_bytes(160 + f_idx * 10, 120 + f_idx * 10, color)
                    url = f"{self.base_url}/api/devices/{dev_id}/screen-frame"
                    req = urllib.request.Request(url, data=frame, headers={"Content-Type": "image/jpeg"}, method="POST")
                    with urllib.request.urlopen(req, timeout=5) as resp:
                        if resp.status != 200:
                            errors.append(f"Client {dev_id} frame {f_idx} status {resp.status}")
                    time.sleep(0.02)
            except Exception as e:
                errors.append(f"Client {dev_id} exception: {e}")

        def dashboard_reader(dev_id):
            try:
                for _ in range(8):
                    if stop_event.is_set():
                        break
                    url = f"{self.base_url}/api/devices/{dev_id}/screen-frame.jpg"
                    req = urllib.request.Request(url, method="GET")
                    try:
                        with urllib.request.urlopen(req, timeout=5) as resp:
                            if resp.status == 200:
                                b = resp.read()
                                if not b.startswith(b"\xff\xd8\xff") or not b.endswith(b"\xff\xd9"):
                                    errors.append(f"Reader {dev_id} corrupted JPEG markers")
                    except urllib.error.HTTPError as h_err:
                        # 404 je ocekivan ako klijent jos uvek nije poslao prvi frejm
                        if h_err.code != 404:
                            errors.append(f"Reader {dev_id} unexpected HTTP {h_err.code}")
                    time.sleep(0.015)
            except Exception as e:
                errors.append(f"Reader {dev_id} exception: {e}")

        def fleet_poller():
            try:
                for _ in range(10):
                    if stop_event.is_set():
                        break
                    with urllib.request.urlopen(f"{self.base_url}/api/devices", timeout=5) as resp:
                        if resp.status != 200:
                            errors.append(f"Fleet poller status {resp.status}")
                        else:
                            _ = json.loads(resp.read().decode("utf-8"))
                    time.sleep(0.02)
            except Exception as e:
                errors.append(f"Fleet poller exception: {e}")

        threads = []
        # Pokrecemo klijente
        for dev in client_devices:
            t = threading.Thread(target=client_worker, args=(dev,))
            threads.append(t)
        # Pokrecemo radnika na shared uredjaju
        threads.append(threading.Thread(target=client_worker, args=(shared_device,)))

        # Pokrecemo dashboard citace
        for dev in client_devices:
            t = threading.Thread(target=dashboard_reader, args=(dev,))
            threads.append(t)
        threads.append(threading.Thread(target=dashboard_reader, args=(shared_device,)))

        # Pokrecemo periodican fleet poller
        threads.append(threading.Thread(target=fleet_poller))

        # Start all
        for t in threads:
            t.start()

        # Join all
        for t in threads:
            t.join(timeout=10.0)

        self.assertEqual(errors, [], f"Konkurentni test je zabelezio greske: {errors}")

    def test_12_advanced_edge_cases_and_security(self):
        """
        Provera zastite od nevalidnih formata, praznih tela, nepodrzanih metoda i zlonamernih ulaza:
        - Odbijanje nedozvoljenih Content-Type-ova (image/png, application/json, text/html)
        - Prazno telo zahteva i missing Content-Length
        - Whitespace / prazan device_id u putanji (%20)
        - Graciozno rukovanje nevalidnim X-Client-Timestamp zaglavljem
        - Headeri kesiranja na GET odzivu (no-cache, no-store, X-Frame-Timestamp)
        """
        device_id = "DEV-SECURITY-CHECKS"
        jpeg_bytes = create_sample_jpeg_bytes(100, 100, "white")
        url_post = f"{self.base_url}/api/devices/{device_id}/screen-frame"

        # 1. Odbijanje image/png
        req_png = urllib.request.Request(url_post, data=jpeg_bytes, headers={"Content-Type": "image/png"}, method="POST")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_png, timeout=5)
        self.assertEqual(ctx.exception.code, 400)
        err = json.loads(ctx.exception.read().decode("utf-8"))
        self.assertIn("Content-Type", err.get("error", ""))

        # 2. Odbijanje application/json na binarnom endpointu
        req_json = urllib.request.Request(url_post, data=b'{"test": 1}', headers={"Content-Type": "application/json"}, method="POST")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_json, timeout=5)
        self.assertEqual(ctx.exception.code, 400)

        # 3. Odbijanje text/html
        req_html = urllib.request.Request(url_post, data=b"<html></html>", headers={"Content-Type": "text/html"}, method="POST")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_html, timeout=5)
        self.assertEqual(ctx.exception.code, 400)

        # 4. Prazan device_id (samo razmaci %20%20)
        url_blank_id = f"{self.base_url}/api/devices/%20%20/screen-frame"
        req_blank_id = urllib.request.Request(url_blank_id, data=jpeg_bytes, headers={"Content-Type": "image/jpeg"}, method="POST")
        with self.assertRaises(urllib.error.HTTPError) as ctx:
            urllib.request.urlopen(req_blank_id, timeout=5)
        self.assertEqual(ctx.exception.code, 400)
        err_blank = json.loads(ctx.exception.read().decode("utf-8"))
        self.assertIn("device_id", err_blank.get("error", ""))

        # GET sa praznim device_id (%20%20) takodje mora vratiti 400
        url_blank_get = f"{self.base_url}/api/devices/%20%20/screen-frame.jpg"
        req_blank_get = urllib.request.Request(url_blank_get, method="GET")
        with self.assertRaises(urllib.error.HTTPError) as ctx_get:
            urllib.request.urlopen(req_blank_get, timeout=5)
        self.assertEqual(ctx_get.exception.code, 400)

        # 5. Nevalidan X-Client-Timestamp (string umesto float) mora proci sa fallback-om na serversko vreme
        req_bad_ts = urllib.request.Request(
            url_post,
            data=jpeg_bytes,
            headers={"Content-Type": "image/jpeg", "X-Client-Timestamp": "not-a-number"},
            method="POST",
        )
        with urllib.request.urlopen(req_bad_ts, timeout=5) as resp:
            self.assertEqual(resp.status, 200)
            data = json.loads(resp.read().decode("utf-8"))
            self.assertIsInstance(data.get("timestamp"), float)
            self.assertAlmostEqual(data.get("timestamp"), time.time(), delta=3.0)

        # 6. Provera kompletnih bezbednosnih i cache kontrolnih hedera na GET odgovoru
        url_get = f"{self.base_url}/api/devices/{device_id}/screen-frame.jpg"
        with urllib.request.urlopen(url_get, timeout=5) as resp_get:
            self.assertEqual(resp_get.status, 200)
            self.assertEqual(resp_get.headers.get("Cache-Control"), "no-cache, no-store, must-revalidate")
            self.assertEqual(resp_get.headers.get("Pragma"), "no-cache")
            self.assertEqual(resp_get.headers.get("Expires"), "0")
            self.assertEqual(resp_get.headers.get("Access-Control-Allow-Origin"), "*")
            self.assertIsNotNone(resp_get.headers.get("X-Frame-Timestamp"))
            float(resp_get.headers.get("X-Frame-Timestamp"))


if __name__ == "__main__":
    unittest.main()

