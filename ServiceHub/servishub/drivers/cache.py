"""
servishub.drivers.cache
=======================
Upravljanje trajnim lokalnim keširanjem OEM drajvera na disku.
Putanja: storage/drivers_cache/ (<hash>.cab i <hash>_meta.json).
"""

import os
import sys
import json
import re
import shutil
import hashlib
import subprocess
import tempfile
import threading
from datetime import datetime
from servishub.drivers.hwid import generate_hwid_candidates


_INF_HWID_PATTERN = re.compile(
    r"(?i)\b("
    r"(?:PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2,4})?)"
    r"|(?:USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(?:&REV_[0-9A-F]{2,4})?(?:&MI_[0-9A-F]{2})?)"
    r"|(?:(?:HDAUDIO|INTELAUDIO)\\FUNC_\d+&VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2,4})?)"
    r"|(?:ACPI\\VEN_[A-Z0-9_]{3,6}&DEV_[A-Z0-9_]{3,6})"
    r"|(?:ACPI\\[A-Z]{3,4}[0-9A-Z]{4})"
    r")"
)
_MAX_INF_EXTRACT_BYTES = 4 * 1024 * 1024


def _decode_inf_bytes(raw: bytes) -> str:
    if not raw:
        return ""
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")) or raw[:200].count(b"\x00") > 10:
        try:
            return raw.decode("utf-16", errors="ignore")
        except Exception:
            pass
    try:
        return raw.decode("utf-8", errors="ignore")
    except Exception:
        return raw.decode("latin-1", errors="ignore")


def extract_inf_hwids_from_cab(cab_data: bytes) -> list[str]:
    """
    Bezbedno ekstrahuje deklarisane HWID-ove iz .inf datoteka unutar .cab arhive
    pomoću bsdtar-a (ako je dostupan), bez pisanja raspakovanih fajlova u keš.
    """
    if not cab_data or not isinstance(cab_data, (bytes, bytearray)):
        return []
    bsdtar_bin = shutil.which("bsdtar")
    if not bsdtar_bin:
        return []

    tmp_cab = None
    try:
        with tempfile.NamedTemporaryFile(prefix="wis-cab-", suffix=".cab", delete=False) as tf:
            tf.write(cab_data)
            tmp_cab = tf.name

        proc = subprocess.run(
            [bsdtar_bin, "-xOf", tmp_cab, "*.inf", "*.INF"],
            capture_output=True,
            timeout=10,
            check=False,
        )
        raw_out = (proc.stdout or b"")[:_MAX_INF_EXTRACT_BYTES]
        if not raw_out:
            return []

        text = _decode_inf_bytes(raw_out)
        found = []
        seen = set()
        for match in _INF_HWID_PATTERN.findall(text):
            cleaned = re.sub(r"\\+", r"\\", match.strip().upper())
            if cleaned and cleaned not in seen:
                seen.add(cleaned)
                found.append(cleaned)
        return found
    except Exception:
        return []
    finally:
        if tmp_cab and os.path.exists(tmp_cab):
            try:
                os.remove(tmp_cab)
            except OSError:
                pass


def sanitize_id(dev_id: str) -> str:
    """Sanitizuje ID / ime uređaja za bezbedno korišćenje u putanjama."""
    if not dev_id:
        return "UNKNOWN"
    return "".join(c if c.isalnum() or c in "-_" else "_" for c in dev_id)


class DriverCacheManager:
    """
    Upravlja trajnim lokalnim keširanjem OEM drajvera na disku.
    Putanja: storage/drivers_cache/ (<hash>.cab i <hash>_meta.json).
    Ključ: SHA-256 heš normalizovanog malim slovima HWID-ja.
    """
    def __init__(self, cache_dir: str, on_driver_cached=None):
        self.cache_dir = os.path.abspath(cache_dir)
        self.on_driver_cached = on_driver_cached
        self.lock = threading.Lock()
        self.inf_index_path = os.path.join(self.cache_dir, "inf_hwid_index.json")
        os.makedirs(self.cache_dir, exist_ok=True)

    def _load_inf_index_locked(self) -> dict:
        if not os.path.isfile(self.inf_index_path):
            return {}
        try:
            with open(self.inf_index_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

    def _save_inf_index_locked(self, index_data: dict) -> None:
        tmp_idx = self.inf_index_path + ".tmp"
        try:
            with open(tmp_idx, "w", encoding="utf-8") as f:
                json.dump(index_data, f, indent=2, ensure_ascii=False)
                f.flush()
                os.fsync(f.fileno())
            os.replace(tmp_idx, self.inf_index_path)
        except Exception:
            if os.path.exists(tmp_idx):
                try:
                    os.remove(tmp_idx)
                except OSError:
                    pass

    @staticmethod
    def normalize_hwid(hwid: str) -> str:
        if not hwid:
            return ""
        return re.sub(r'\\+', r'\\', hwid.strip().lower())

    def hwid_hash(self, hwid: str) -> str:
        norm = self.normalize_hwid(hwid)
        return hashlib.sha256(norm.encode("utf-8")).hexdigest()

    def get_cached(self, hwid: str) -> dict | None:
        """
        Proverava lokalni keš za dati HWID i njegove fallback forme.
        Prvo traži direktan/alias pogodak, a zatim sekundarni cross-model pogodak
        iz indeksa deklarisanih .inf HWID-ova.
        """
        candidates = generate_hwid_candidates(hwid)
        with self.lock:
            for cand in candidates:
                h = self.hwid_hash(cand)
                cab_path = os.path.join(self.cache_dir, f"{h}.cab")
                meta_path = os.path.join(self.cache_dir, f"{h}_meta.json")

                if os.path.isfile(cab_path) and os.path.isfile(meta_path):
                    try:
                        if os.path.getsize(cab_path) > 0:
                            with open(meta_path, "r", encoding="utf-8") as f:
                                meta = json.load(f)
                            meta["driver_id"] = h
                            meta["cab_path"] = cab_path
                            meta.setdefault("match_source", "primary_cache")
                            return meta
                    except Exception:
                        pass

            inf_index = self._load_inf_index_locked()
            if inf_index:
                for cand in candidates:
                    norm_cand = self.normalize_hwid(cand)
                    drv_id = inf_index.get(norm_cand)
                    if not drv_id:
                        continue
                    cab_path = os.path.join(self.cache_dir, f"{drv_id}.cab")
                    meta_path = os.path.join(self.cache_dir, f"{drv_id}_meta.json")
                    if os.path.isfile(cab_path) and os.path.isfile(meta_path):
                        try:
                            if os.path.getsize(cab_path) > 0:
                                with open(meta_path, "r", encoding="utf-8") as f:
                                    meta = json.load(f)
                                meta["driver_id"] = drv_id
                                meta["cab_path"] = cab_path
                                meta["match_source"] = "inf_cross_model"
                                meta["matched_inf_hwid"] = cand
                                return meta
                        except Exception:
                            pass
        return None

    def save_cache(self, hwid: str, cab_data: bytes, meta: dict, alias_hwids: list = None) -> dict:
        """
        Čuva .cab arhivu i meta JSON na disk i kreira hardlink/kopiju za povezana alijas imena.
        """
        h = self.hwid_hash(hwid)
        cab_path = os.path.join(self.cache_dir, f"{h}.cab")
        meta_path = os.path.join(self.cache_dir, f"{h}_meta.json")

        sha256_hash = hashlib.sha256(cab_data).hexdigest()
        size_bytes = len(cab_data)

        date_val = meta.get("date_str") or (
            meta.get("date").strftime("%m/%d/%Y")
            if isinstance(meta.get("date"), datetime)
            else str(meta.get("date", ""))
        )

        saved_meta = {
            "driver_id": h,
            "hwid": hwid,
            "normalized_hwid": self.normalize_hwid(hwid),
            "matched_query": meta.get("matched_query", hwid),
            "title": meta.get("title", ""),
            "version": meta.get("version", ""),
            "date": date_val,
            "device_class": meta.get("device_class", ""),
            "products": meta.get("products", ""),
            "size_bytes": size_bytes,
            "sha256": sha256_hash,
            "source_url": meta.get("download_url", ""),
            "cached_at": datetime.now().isoformat()
        }

        with self.lock:
            tmp_cab = cab_path + ".tmp"
            with open(tmp_cab, "wb") as f:
                f.write(cab_data)
            os.replace(tmp_cab, cab_path)

            tmp_meta = meta_path + ".tmp"
            with open(tmp_meta, "w", encoding="utf-8") as f:
                json.dump(saved_meta, f, indent=2, ensure_ascii=False)
            os.replace(tmp_meta, meta_path)

            saved_meta["cab_path"] = cab_path

            all_aliases = set(alias_hwids or [])
            all_aliases.add(hwid)
            if "matched_query" in meta and meta["matched_query"]:
                all_aliases.add(meta["matched_query"])

            for alias in all_aliases:
                if not alias:
                    continue
                alias_h = self.hwid_hash(alias)
                if alias_h != h:
                    a_cab = os.path.join(self.cache_dir, f"{alias_h}.cab")
                    a_meta = os.path.join(self.cache_dir, f"{alias_h}_meta.json")
                    try:
                        if not os.path.isfile(a_cab):
                            try:
                                os.link(cab_path, a_cab)
                            except OSError:
                                shutil.copyfile(cab_path, a_cab)
                        m_copy = dict(saved_meta)
                        m_copy["driver_id"] = alias_h
                        m_copy["hwid"] = alias
                        m_copy["normalized_hwid"] = self.normalize_hwid(alias)
                        m_copy["alias_of"] = h
                        tmp_ameta = a_meta + ".tmp"
                        with open(tmp_ameta, "w", encoding="utf-8") as f:
                            json.dump(m_copy, f, indent=2, ensure_ascii=False)
                        os.replace(tmp_ameta, a_meta)
                    except Exception as e:
                        print(f"[WARN] Neuspeh povezivanja keš alijasa za {alias}: {e}", file=sys.stderr)

            inf_hwids = extract_inf_hwids_from_cab(cab_data)
            if inf_hwids:
                inf_index = self._load_inf_index_locked()
                changed = False
                for ih in inf_hwids:
                    variants = [ih]
                    no_rev = re.sub(r"&REV_[0-9A-F]{2,4}", "", ih, flags=re.IGNORECASE)
                    if no_rev and no_rev != ih:
                        variants.append(no_rev)
                    for v in variants:
                        norm_ih = self.normalize_hwid(v)
                        if norm_ih and norm_ih not in inf_index:
                            inf_index[norm_ih] = h
                            changed = True
                if changed:
                    self._save_inf_index_locked(inf_index)

        print(f"[+] [DriverCache] Keširan drajver '{saved_meta['title']}' ({size_bytes} B, ID: {h[:12]}...) za HWID: {hwid}")
        cb = getattr(self, "on_driver_cached", None)
        if callable(cb):
            try:
                cb(cab_path, saved_meta)
            except Exception:
                pass
        return saved_meta

    def list_cached(self) -> list:
        """Vraća listu metapodataka za sve keširane drajvere koji imaju validan .cab fajl."""
        items = []
        with self.lock:
            if not os.path.isdir(self.cache_dir):
                return items
            seen_hashes = set()
            for fname in sorted(os.listdir(self.cache_dir)):
                if fname.endswith("_meta.json"):
                    meta_path = os.path.join(self.cache_dir, fname)
                    try:
                        with open(meta_path, "r", encoding="utf-8") as f:
                            data = json.load(f)
                        drv_id = data.get("driver_id")
                        if drv_id and drv_id not in seen_hashes:
                            cab_file = os.path.join(self.cache_dir, f"{drv_id}.cab")
                            if os.path.isfile(cab_file) and os.path.getsize(cab_file) > 0:
                                data["cab_path"] = cab_file
                                items.append(data)
                                seen_hashes.add(drv_id)
                    except Exception:
                        pass
        return items

    def get_cab_path(self, driver_id: str) -> str | None:
        """Vraća punu putanju do .cab fajla ako postoji."""
        safe_id = sanitize_id(driver_id)
        p = os.path.join(self.cache_dir, f"{safe_id}.cab")
        if os.path.isfile(p) and os.path.getsize(p) > 0:
            return p
        return None
