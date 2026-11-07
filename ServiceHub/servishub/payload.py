"""Read-only pristup poslednjem proverenom potpisanom WIS izdanju."""
import json
import os
from pathlib import Path
import threading
from servishub.releases import ReleaseError, verify_release


class PayloadManager:
    def __init__(self, base_dir, cache_dir=None, source_dir=None, public_key=None):
        self.base_dir = os.path.abspath(base_dir)
        self.cache_dir = os.path.abspath(cache_dir or os.path.join(self.base_dir, "storage", "cache"))
        # Parametar ostaje kompatibilan, ali izvori ne utiču na objavljeni release.
        self.source_dir = source_dir
        self.public_key = Path(public_key or os.environ.get("SERVIS_HUB_PUBLIC_KEY") or
                               Path(__file__).resolve().parents[1] / "trusted_public_key.xml")
        self.lock = threading.Lock()
        self._fingerprint = None
        self._snapshot = None
        self.payload_zip_path = str(Path(self.cache_dir) / "wis-payload.zip")
        self.wis_zip_path = self.payload_zip_path
        self.payload_meta_path = str(Path(self.cache_dir) / "payload_meta.json")

    def _paths(self):
        cache = Path(self.cache_dir)
        pointer = cache / "current.json"
        if pointer.exists():
            if pointer.stat().st_size > 4096:
                raise ReleaseError("Neispravan current.json.")
            release_id = json.loads(pointer.read_text())["release_id"]
            import re
            if not isinstance(release_id, str) or not re.fullmatch(r"[0-9a-f]{40}-[0-9a-f]{16}", release_id):
                raise ReleaseError("Neispravan release ID.")
            directory = cache / "releases" / release_id
            if directory.is_symlink() or directory.resolve().parent != (cache / "releases").resolve():
                raise ReleaseError("Release putanja izlazi iz skladišta.")
            return directory / "wis-payload.zip", directory / "payload_meta.json", pointer
        # Prelazni read-only pristup postojećem, potpisanom schema2 cache-u.
        return cache / "wis-payload.zip", cache / "payload_meta.json", None

    def _load(self):
        try:
            zip_path, meta_path, pointer = self._paths()
            paths = [zip_path, meta_path, self.public_key] + ([pointer] if pointer else [])
            fingerprint = tuple((str(p), p.stat().st_dev, p.stat().st_ino,
                                 p.stat().st_mtime_ns, p.stat().st_ctime_ns, p.stat().st_size) for p in paths)
            if fingerprint != self._fingerprint:
                meta = verify_release(zip_path, meta_path, self.public_key)
                self._snapshot = (meta, str(zip_path))
                self._fingerprint = fingerprint
            self.payload_zip_path = str(zip_path)
            self.payload_meta_path = str(meta_path)
            return self._snapshot
        except ReleaseError:
            raise
        except Exception as e:
            raise ReleaseError("Potpisano izdanje nije dostupno ili nije validno.") from e

    def ensure_payload(self, force=False):
        if force:
            raise ReleaseError("Server ne pravi pakete. Koristite Skripte/Release-WIS.py.")
        with self.lock:
            meta, _ = self._load()
            return json.loads(json.dumps(meta))

    def get_metadata(self):
        return self.ensure_payload()

    def get_payload_file(self):
        with self.lock:
            meta, path = self._load()
            return json.loads(json.dumps(meta)), path
