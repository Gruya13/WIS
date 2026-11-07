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
import hashlib
import threading
from datetime import datetime
from servishub.drivers.hwid import generate_hwid_candidates


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
    def __init__(self, cache_dir: str):
        self.cache_dir = os.path.abspath(cache_dir)
        self.lock = threading.Lock()
        os.makedirs(self.cache_dir, exist_ok=True)

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
        Vraća meta rečnik ako postoji ispravan .cab i _meta.json.
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
                                import shutil
                                shutil.copyfile(cab_path, a_cab)
                        m_copy = dict(saved_meta)
                        m_copy["driver_id"] = alias_h
                        m_copy["hwid"] = alias
                        m_copy["normalized_hwid"] = self.normalize_hwid(alias)
                        m_copy["alias_of"] = h
                        with open(a_meta, "w", encoding="utf-8") as f:
                            json.dump(m_copy, f, indent=2, ensure_ascii=False)
                    except Exception as e:
                        print(f"[WARN] Neuspeh povezivanja keš alijasa za {alias}: {e}", file=sys.stderr)

        print(f"[+] [DriverCache] Keširan drajver '{saved_meta['title']}' ({size_bytes} B, ID: {h[:12]}...) za HWID: {hwid}")
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
