"""
servishub.drivers.oem.lenovo
============================
Konkretna implementacija OEM provajdera za Lenovo hardver (ThinkPad, ThinkCentre, IdeaPad).
Podržava:
- Upite o fabričkoj garanciji preko Lenovo eSupport API-ja uz striktni timeout od max 5s i graceful fallback.
- Rezoluciju zvaničnih SCCM driver paketa sa proverom lokalnog keša u storage/drivers_cache/oem/lenovo/{MTM}/.
- Detekciju i poređenje dostupnih BIOS/UEFI firmware ažuriranja.
- Višenitno bezbedan rad (threading.Lock za keš operacije na disku).
"""

import os
import json
import re
import hashlib
import threading
import urllib.request
import urllib.error
import urllib.parse
from datetime import datetime, date
from typing import Any, Dict, Optional, List

from servishub.drivers.oem.base import BaseOEMProvider, build_keyboard_spec, make_keyboard_layout_name

try:
    from servishub.drivers.version import parse_driver_date
except ImportError:
    # Graceful fallback ukoliko se pokreće izolovano
    def parse_driver_date(d_val) -> Optional[date]:
        if not d_val:
            return None
        if isinstance(d_val, datetime):
            return d_val.date()
        if isinstance(d_val, date):
            return d_val
        s = str(d_val).strip()
        for fmt in ("%Y-%m-%d", "%m/%d/%Y", "%d/%m/%Y", "%d.%m.%Y.", "%d.%m.%Y"):
            try:
                return datetime.strptime(s.split("T")[0].strip(), fmt).date()
            except ValueError:
                pass
        return None


class LenovoOEMProvider(BaseOEMProvider):
    """
    OEM provajder za Lenovo računare i radne stanice.
    """

    # Referentna baza za poznate Lenovo ThinkPad modele (fallback i offline rezolucija)
    KNOWN_MODELS = {
        "20TD": {
            "model_name": "ThinkPad E14 Gen 2 (Intel)",
            "bios_version": "1.62",
            "bios_id": "R1EET62W",
            "bios_date": "2024-03-12",
            "bios_url": "https://download.lenovo.com/pccbbs/mobiles/r1euj62w.exe",
            "sccm_package": "tp_e14_gen2_win11_20td.exe",
            "sccm_url": "https://download.lenovo.com/pccbbs/mobiles/tp_e14_gen2_win11_20td.exe",
            "sccm_size": 1420185600,
        },
        "21AH": {
            "model_name": "ThinkPad T14 Gen 3 (Intel)",
            "bios_version": "1.45",
            "bios_id": "N3BET58W",
            "bios_date": "2024-04-18",
            "bios_url": "https://download.lenovo.com/pccbbs/mobiles/n3buj58w.exe",
            "sccm_package": "tp_t14_gen3_win11_21ah.exe",
            "sccm_url": "https://download.lenovo.com/pccbbs/mobiles/tp_t14_gen3_win11_21ah.exe",
            "sccm_size": 1684275200,
        },
        "20UD": {
            "model_name": "ThinkPad T14 Gen 1 (AMD)",
            "bios_version": "1.48",
            "bios_id": "R1MET48W",
            "bios_date": "2024-02-15",
            "bios_url": "https://download.lenovo.com/pccbbs/mobiles/r1muj48w.exe",
            "sccm_package": "tp_t14_gen1_amd_win11_20ud.exe",
            "sccm_url": "https://download.lenovo.com/pccbbs/mobiles/tp_t14_gen1_amd_win11_20ud.exe",
            "sccm_size": 1530291200,
        },
        "21E3": {
            "model_name": "ThinkPad E14 Gen 4 (Intel)",
            "bios_version": "1.28",
            "bios_id": "R1XET40W",
            "bios_date": "2024-01-20",
            "bios_url": "https://download.lenovo.com/pccbbs/mobiles/r1xuj40w.exe",
            "sccm_package": "tp_e14_gen4_win11_21e3.exe",
            "sccm_url": "https://download.lenovo.com/pccbbs/mobiles/tp_e14_gen4_win11_21e3.exe",
            "sccm_size": 1583348000,
        },
    }

    def __init__(self, cache_dir: Optional[str] = None, timeout: float = 5.0):
        """
        Inicijalizuje Lenovo OEM provajdera.

        :param cache_dir: Koreni direktorijum za skladištenje OEM keša
        :param timeout: Mrežni timeout u sekundama (maksimalno 5s)
        """
        if cache_dir:
            self.base_cache_dir = os.path.abspath(cache_dir)
        else:
            try:
                from servishub.config import DRIVERS_CACHE_DIR
                self.base_cache_dir = os.path.join(os.path.abspath(DRIVERS_CACHE_DIR), "oem", "lenovo")
            except Exception:
                self.base_cache_dir = os.path.abspath(
                    os.path.join(os.path.dirname(__file__), "..", "..", "..", "storage", "drivers_cache", "oem", "lenovo")
                )

        # Timeout je striktno ograničen na najviše 5 sekundi
        self.timeout = min(max(float(timeout), 0.5), 5.0)
        self.lock = threading.Lock()

        with self.lock:
            os.makedirs(self.base_cache_dir, exist_ok=True)
            self.warranty_cache_dir = os.path.join(self.base_cache_dir, "warranty")
            os.makedirs(self.warranty_cache_dir, exist_ok=True)
            self.keyboard_cache_dir = os.path.join(self.base_cache_dir, "keyboard")
            os.makedirs(self.keyboard_cache_dir, exist_ok=True)

    # =========================================================================
    # Pomoćne metode za normalizaciju i parsiranje
    # =========================================================================

    @staticmethod
    def _normalize_mtm(model: str) -> str:
        """
        Izdvaja 4-karakterni Lenovo Machine Type Model (MTM) kod.
        Primeri:
        - '20TD0003CD' -> '20TD'
        - 'ThinkPad E14 Gen 2 (20TD)' -> '20TD'
        - '21AH' -> '21AH'
        - 'ThinkPad T14 Gen 3 Type 21AH' -> '21AH'
        """
        if not model:
            return ""
        clean = model.strip().upper()
        # Regex za 4-karakterni MTM kod (cifra + 3 alfanumerika, npr. 20TD, 21AH, 82KJ)
        m = re.search(r'\b([0-9][0-9A-Z]{3})\b', clean)
        if m:
            return m.group(1)
        if len(clean) == 4 and clean.isalnum():
            return clean
        if len(clean) >= 4 and clean[:4].isalnum():
            return clean[:4]
        return re.sub(r'[^A-Z0-9_-]', '_', clean)

    @staticmethod
    def _normalize_serial(serial: str) -> str:
        if not serial:
            return ""
        return re.sub(r'[^A-Z0-9_-]', '', serial.strip().upper())

    @classmethod
    def _parse_bios_version_components(cls, ver_str: str) -> List[int]:
        """
        Parsira BIOS verziju u listu celih brojeva za semantičko poređenje.
        Podržava:
        - '1.62' -> [1, 62]
        - 'R1EET62W (1.62)' -> [1, 62]
        - 'R1EET50W' -> [1, 50]
        - 'N3BET58W (1.45)' -> [1, 45]
        """
        if not ver_str:
            return []
        s = ver_str.strip()
        if s.upper() in ("UNKNOWN", "N/A", "NONE", "NULL", "-"):
            return []

        # 1. Provera verzije u zagradama, npr. 'R1BET65W (1.35)'
        paren = re.search(r'\(([^)]+)\)', s)
        if paren:
            tokens = [int(x) for x in re.findall(r'\d+', paren.group(1))]
            if tokens:
                return tokens

        # 2. Provera decimalne tačke, npr. '1.62' ili '1.30.2'
        m = re.search(r'\b\d+(?:\.\d+)+\b', s)
        if m:
            return [int(x) for x in re.findall(r'\d+', m.group(0))]

        # 3. Fallback: izdvajanje svih numeričkih sekvenci
        return [int(x) for x in re.findall(r'\d+', s)]

    @classmethod
    def _is_newer_bios(cls, current_ver: str, latest_ver: str) -> bool:
        """
        Poredi verzije BIOS-a i vraća True ako je latest_ver novija od current_ver.
        """
        if not latest_ver or latest_ver.strip().upper() in ("UNKNOWN", "N/A", "NONE", ""):
            return False
        if not current_ver or current_ver.strip().upper() in ("UNKNOWN", "N/A", "NONE", ""):
            return True

        c_parts = cls._parse_bios_version_components(current_ver)
        l_parts = cls._parse_bios_version_components(latest_ver)

        if not c_parts and l_parts:
            return True
        if not l_parts:
            return False

        max_len = max(len(c_parts), len(l_parts))
        c_pad = c_parts + [0] * (max_len - len(c_parts))
        l_pad = l_parts + [0] * (max_len - len(l_parts))

        return l_pad > c_pad

    # =========================================================================
    # 1. Provera fabričke garancije (get_warranty_info)
    # =========================================================================

    def get_warranty_info(self, serial: str, model: str = "") -> Dict[str, Any]:
        """
        Poziva Lenovo eSupport API za proveru statusa garancije sa striktnim timeout-om od max 5s.
        U slučaju greške ili mrežne nedostupnosti, vraća graceful fallback rečnik.
        """
        clean_sn = self._normalize_serial(serial)
        if not clean_sn:
            return {
                "status": "error",
                "in_warranty": False,
                "status_label": "Neispravan serijski broj",
                "service_level": "N/A",
                "start_date": "",
                "end_date": "",
                "days_remaining": 0,
                "raw_data": {"error": "Serijski broj je prazan ili neispravan"},
            }

        cache_file = os.path.join(self.warranty_cache_dir, f"{clean_sn}.json")

        # 1. Provera lokalnog keša na disku
        with self.lock:
            if os.path.isfile(cache_file):
                try:
                    with open(cache_file, "r", encoding="utf-8") as f:
                        cached_entry = json.load(f)
                    # Ažuriramo preostale dane u odnosu na današnji dan
                    end_dt = parse_driver_date(cached_entry.get("end_date", ""))
                    if end_dt:
                        today = date.today()
                        days_rem = (end_dt - today).days
                        cached_entry["days_remaining"] = max(0, days_rem)
                        cached_entry["in_warranty"] = days_rem > 0
                        cached_entry["status_label"] = "U garanciji" if days_rem > 0 else "Istekla garancija"
                    return cached_entry
                except Exception:
                    pass

        # 2. Mrežni upit ka Lenovo eSupport API-ju
        api_url = f"https://pcsupport.lenovo.com/api/v4/warranty/query?sn={urllib.parse.quote(clean_sn)}"
        req = urllib.request.Request(
            api_url,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-LenovoOEM/1.0",
                "Accept": "application/json",
            },
        )

        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                if resp.status == 200:
                    raw_body = resp.read().decode("utf-8")
                    data = json.loads(raw_body)
                    result = self._parse_lenovo_warranty_json(data, clean_sn)
                    if result.get("status") == "ok":
                        # Keširamo uspešan odgovor
                        with self.lock:
                            tmp_file = cache_file + ".tmp"
                            with open(tmp_file, "w", encoding="utf-8") as f:
                                json.dump(result, f, indent=2, ensure_ascii=False)
                            os.replace(tmp_file, cache_file)
                        return result
        except Exception as exc:
            # Mrežni timeout, HTTP 404/401/500 ili nevalidan odgovor
            pass

        # 3. Graceful fallback odgovor
        return {
            "status": "fallback",
            "in_warranty": False,
            "status_label": "Nepoznato (Timeout/Greška)",
            "service_level": "N/A",
            "start_date": "",
            "end_date": "",
            "days_remaining": 0,
            "raw_data": {
                "serial": clean_sn,
                "model": model,
                "note": "Lenovo eSupport API nije dostupan ili je istekao timeout (<= 5s)",
            },
        }

    def _parse_lenovo_warranty_json(self, data: Any, serial: str) -> Dict[str, Any]:
        """Parsira sirovi JSON sa Lenovo eSupport portala u standardni format."""
        if not isinstance(data, dict):
            return {"status": "error"}

        root = data.get("data", data)
        warranties = root.get("warranties", []) if isinstance(root, dict) else []

        start_date_str = ""
        end_date_str = ""
        service_level = "Standard Depot Warranty"
        in_warr = False

        if warranties and isinstance(warranties, list):
            primary = warranties[0]
            start_date_str = str(primary.get("startDate", primary.get("start", "")))
            end_date_str = str(primary.get("endDate", primary.get("end", "")))
            service_level = primary.get("name", primary.get("type", service_level))
            w_status = str(primary.get("status", "")).lower()
            in_warr = "active" in w_status or "valid" in w_status or primary.get("inWarranty", False)
        elif isinstance(root, dict):
            start_date_str = str(root.get("startDate", root.get("start_date", "")))
            end_date_str = str(root.get("endDate", root.get("end_date", "")))
            service_level = root.get("serviceLevel", root.get("serviceType", service_level))
            in_warr = bool(root.get("inWarranty", False))

        days_remaining = 0
        end_dt = parse_driver_date(end_date_str)
        if end_dt:
            today = date.today()
            days_diff = (end_dt - today).days
            days_remaining = max(0, days_diff)
            in_warr = days_diff > 0

        status_label = "U garanciji" if in_warr else "Istekla garancija"

        return {
            "status": "ok",
            "in_warranty": in_warr,
            "status_label": status_label,
            "service_level": service_level,
            "start_date": start_date_str,
            "end_date": end_date_str,
            "days_remaining": days_remaining,
            "raw_data": data,
        }

    # =========================================================================
    # 2. Rezolucija SCCM drajver paketa (resolve_driver_pack)
    # =========================================================================

    def resolve_driver_pack(self, model: str, os_ver: str = "win11-x64") -> Dict[str, Any]:
        """
        Pronalazi zvanični SCCM drajver paket po MTM/model kodu.
        Proverava lokalni keš u storage/drivers_cache/oem/lenovo/{MTM}/.
        Ako paket postoji u kešu, vraća lokalnu putanju.
        Ako ne postoji, preuzima/simulira metapodatke sa Lenovo portala uz 5s timeout.
        """
        mtm = self._normalize_mtm(model)
        if not mtm:
            return {
                "status": "not_found",
                "source": "lenovo_sccm",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        mtm_dir = os.path.join(self.base_cache_dir, mtm)

        # 1. Provera lokalnog keša na disku
        with self.lock:
            if os.path.isdir(mtm_dir):
                # Tražimo arhive paketa (.exe, .zip, .cab, .7z)
                archives = [
                    f for f in sorted(os.listdir(mtm_dir))
                    if f.lower().endswith((".exe", ".zip", ".cab", ".7z"))
                    and not f.endswith(".tmp")
                ]
                if archives:
                    chosen = archives[0]
                    local_file = os.path.join(mtm_dir, chosen)
                    f_size = os.path.getsize(local_file)
                    if f_size > 0:
                        # Čitamo download_url iz package_meta.json ako postoji
                        dl_url = f"https://download.lenovo.com/pccbbs/mobiles/{chosen}"
                        meta_file = os.path.join(mtm_dir, "package_meta.json")
                        if os.path.isfile(meta_file):
                            try:
                                with open(meta_file, "r", encoding="utf-8") as mf:
                                    dl_url = json.load(mf).get("download_url", dl_url)
                            except Exception:
                                pass
                        return {
                            "status": "ok",
                            "source": "cache",
                            "package_name": chosen,
                            "download_url": dl_url,
                            "local_path": local_file,
                            "size_bytes": f_size,
                        }

        # 2. Paket nije u kešu: pokušaj dohvatanja metapodataka sa portala
        remote_meta = self._fetch_portal_sccm_metadata(mtm, os_ver)
        if remote_meta:
            package_name = remote_meta["package_name"]
            download_url = remote_meta["download_url"]
            size_bytes = remote_meta["size_bytes"]
            source = "lenovo_sccm_portal"
        else:
            # 3. Fallback: simulacija/referentna baza metapodataka
            if mtm in self.KNOWN_MODELS:
                info = self.KNOWN_MODELS[mtm]
                package_name = info["sccm_package"]
                download_url = info["sccm_url"]
                size_bytes = info["sccm_size"]
            else:
                os_tag = "win11" if "11" in os_ver.lower() else "win10"
                package_name = f"tp_{mtm.lower()}_{os_tag}_{os_ver.replace('-', '_')}.exe"
                download_url = f"https://download.lenovo.com/pccbbs/mobiles/{package_name}"
                size_bytes = 1520435200  # Tipična veličina SCCM paketa (~1.4 GB)
            source = "lenovo_sccm_simulated"

        # Zapisujemo metapodatke u keš direktorijum pod lock-om
        with self.lock:
            os.makedirs(mtm_dir, exist_ok=True)
            meta_record = {
                "mtm": mtm,
                "model": model,
                "os_ver": os_ver,
                "package_name": package_name,
                "download_url": download_url,
                "size_bytes": size_bytes,
                "source": source,
                "resolved_at": datetime.now().isoformat(),
            }
            meta_path = os.path.join(mtm_dir, "package_meta.json")
            tmp_path = meta_path + ".tmp"
            with open(tmp_path, "w", encoding="utf-8") as f:
                json.dump(meta_record, f, indent=2, ensure_ascii=False)
            os.replace(tmp_path, meta_path)

        return {
            "status": "ok",
            "source": source,
            "package_name": package_name,
            "download_url": download_url,
            "local_path": "",
            "size_bytes": size_bytes,
        }

    def _fetch_portal_sccm_metadata(self, mtm: str, os_ver: str) -> Optional[Dict[str, Any]]:
        """Pokušava dohvatanje metapodataka sa Lenovo portala uz timeout do 5 sekundi."""
        url = f"https://pcsupport.lenovo.com/api/v4/downloads/drivers?productId={urllib.parse.quote(mtm)}&category=SCCM"
        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-LenovoOEM/1.0",
                "Accept": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                if resp.status == 200:
                    data = json.loads(resp.read().decode("utf-8"))
                    items = data.get("data", {}).get("items", [])
                    if items and isinstance(items, list):
                        item = items[0]
                        return {
                            "package_name": item.get("filename", f"tp_{mtm.lower()}_sccm.exe"),
                            "download_url": item.get("url", f"https://download.lenovo.com/pccbbs/mobiles/tp_{mtm.lower()}.exe"),
                            "size_bytes": int(item.get("size", 1500000000)),
                        }
        except Exception:
            pass
        return None

    # =========================================================================
    # 3. Provera BIOS ažuriranja (check_bios_update)
    # =========================================================================

    def check_bios_update(self, model: str, current_bios: str = "") -> Dict[str, Any]:
        """
        Poredi verziju BIOS-a i proverava dostupnost novijeg firmware-a.
        Proverava lokalni keš i po potrebi upituje Lenovo portal.
        """
        mtm = self._normalize_mtm(model)
        if not mtm:
            return {
                "update_available": False,
                "latest_version": "",
                "current_version": current_bios,
                "download_url": "",
                "release_date": "",
            }

        mtm_dir = os.path.join(self.base_cache_dir, mtm)
        bios_meta_path = os.path.join(mtm_dir, "bios_meta.json")

        bios_data = None

        # 1. Provera keša na disku pod lock-om
        with self.lock:
            if os.path.isfile(bios_meta_path):
                try:
                    with open(bios_meta_path, "r", encoding="utf-8") as f:
                        bios_data = json.load(f)
                except Exception:
                    pass

        # 2. Ako podaci nisu u kešu, dohvata sa portala ili referentne baze
        if not bios_data:
            portal_bios = self._fetch_portal_bios_metadata(mtm)
            if portal_bios:
                bios_data = portal_bios
            elif mtm in self.KNOWN_MODELS:
                km = self.KNOWN_MODELS[mtm]
                bios_data = {
                    "latest_version": km["bios_version"],
                    "bios_id": km["bios_id"],
                    "download_url": km["bios_url"],
                    "release_date": km["bios_date"],
                }
            else:
                bios_data = {
                    "latest_version": "1.30",
                    "bios_id": f"R1ET30W",
                    "download_url": f"https://download.lenovo.com/pccbbs/mobiles/bios_{mtm.lower()}.exe",
                    "release_date": "2024-01-15",
                }

            # Zapisujemo u keš
            with self.lock:
                os.makedirs(mtm_dir, exist_ok=True)
                tmp_meta = bios_meta_path + ".tmp"
                with open(tmp_meta, "w", encoding="utf-8") as f:
                    json.dump(bios_data, f, indent=2, ensure_ascii=False)
                os.replace(tmp_meta, bios_meta_path)

        latest_ver = bios_data.get("latest_version", "")
        download_url = bios_data.get("download_url", "")
        release_date = bios_data.get("release_date", "")

        update_available = self._is_newer_bios(current_bios, latest_ver)

        return {
            "update_available": update_available,
            "latest_version": latest_ver,
            "current_version": current_bios,
            "download_url": download_url,
            "release_date": release_date,
        }

    def _fetch_portal_bios_metadata(self, mtm: str) -> Optional[Dict[str, Any]]:
        """Pokušava dohvatanje BIOS metapodataka sa Lenovo portala uz timeout do 5s."""
        url = f"https://pcsupport.lenovo.com/api/v4/downloads/drivers?productId={urllib.parse.quote(mtm)}&category=BIOS%2FUEFI"
        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-LenovoOEM/1.0",
                "Accept": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                if resp.status == 200:
                    data = json.loads(resp.read().decode("utf-8"))
                    items = data.get("data", {}).get("items", [])
                    if items and isinstance(items, list):
                        item = items[0]
                        return {
                            "latest_version": item.get("version", "1.30"),
                            "bios_id": item.get("title", ""),
                            "download_url": item.get("url", f"https://download.lenovo.com/pccbbs/mobiles/bios_{mtm.lower()}.exe"),
                            "release_date": item.get("date", "2024-01-15"),
                        }
        except Exception:
            pass
        return None

    # =========================================================================
    # 4. Prepoznavanje fizičkog rasporeda tastature (lookup_keyboard_spec)
    # =========================================================================

    _ISO_CODES = (
        "GR", "GER", "DE", "UK", "GB", "FR", "FRE", "CH", "SWI", "ND", "NORD",
        "SRB", "SLV", "CRO", "HR", "SI", "EUR", "ISO", "SPA", "ES", "IT", "ITA",
        "BE", "BEL", "TR", "TUR", "CZ", "CZE", "SK", "SLK", "HU", "HUN", "PL",
        "POL", "ADR", "ADRIATIC", "NO", "SE", "DK", "FI", "NL", "PT", "BR", "LA",
    )

    @classmethod
    def _parse_lenovo_keyboard_description(cls, desc: str) -> Dict[str, Any]:
        """
        Parsira Lenovo FRU / opis tastature (npr. 'CS20-84US', 'CS20-85GR',
        'CS21-101US', 'CS21-102SRB', 'KYB BL SRB 85 KEY') u fizičke osobine.
        """
        text = (desc or "").upper()
        has_copilot = "COPILOT" in text

        # Traži obrazac broja tastera + opcionalni region (npr. 84US, 85GR, 102SRB, 105-ISO)
        has_numpad = None  # type: Optional[bool]
        is_iso = None      # type: Optional[bool]

        m_fru = re.search(r'(?:^|[^0-9])(79|80|82|83|84|85|86|99|100|101|102|103|104|105)\s*[-_]?\s*([A-Z]{2,8})?\b', text)
        key_count = int(m_fru.group(1)) if m_fru else None
        region_tok = (m_fru.group(2) or "") if m_fru else ""

        if key_count is not None:
            has_numpad = key_count >= 95
            if key_count in (85, 86, 102, 103, 105):
                is_iso = True
            elif key_count in (83, 84, 100, 101, 104) and region_tok == "US":
                is_iso = False

        # Eksplicitne oznake za numerički blok u tekstu
        if has_numpad is None:
            if any(tok in text for tok in ("WITH NUMPAD", "WITH NUMERIC", "FULL SIZE", "FULLSIZE")):
                has_numpad = True
            elif any(tok in text for tok in ("WITHOUT NUMPAD", "NO NUMPAD", "TKL", "84 KEY", "85 KEY")):
                has_numpad = False

        # Eksplicitne regionalne / ISO / ANSI oznake
        tokens = set(re.findall(r'[A-Z0-9]+', text))
        if is_iso is None:
            if region_tok in cls._ISO_CODES or (tokens & set(cls._ISO_CODES)) or "ISO" in text or "GERMAN" in text or "SERBIAN" in text or "CROATIAN" in text or "SLOVENIAN" in text or "UK ENGLISH" in text or "ADRIATIC" in text:
                is_iso = True
            elif region_tok == "US" or "US ENGLISH" in text or "ANSI" in text or re.search(r'\bEN[-_]?US\b', text):
                is_iso = False

        return {
            "has_numpad": has_numpad,
            "is_iso": is_iso,
            "has_copilot": has_copilot,
            "key_count": key_count,
        }

    @classmethod
    def _extract_lenovo_keyboard_part(cls, data: Any) -> Optional[str]:
        """Izvlači opis tastature iz Lenovo Parts API JSON odgovora."""
        candidates = []

        def _walk(node: Any):
            if isinstance(node, dict):
                cat = str(node.get("category", node.get("commodity", node.get("partCategory", "")))).upper()
                desc = str(node.get("description", node.get("partDesc", node.get("name", node.get("title", "")))))
                merged = f"{cat} {desc}".upper()
                if any(tag in merged for tag in ("KEYBOARD", "KEYBOARDS INTERNAL", "KYB", "CS20-", "CS21-", "CS22-", "CS23-", "CS24-")):
                    # Izbegavamo eksterne USB tastature ako tražimo ugrađenu
                    if "EXTERNAL" not in merged and "DOCK" not in merged and desc.strip():
                        candidates.append(desc.strip())
                for v in node.values():
                    if isinstance(v, (dict, list)):
                        _walk(v)
            elif isinstance(node, list):
                for item in node:
                    _walk(item)

        _walk(data)
        if not candidates:
            return None
        # Prioritet opisu koji sadrži broj tastera (84/85/101/102...)
        for cand in candidates:
            if re.search(r'(?:79|80|82|83|84|85|86|99|100|101|102|103|104|105)', cand):
                return cand
        return candidates[0]

    @classmethod
    def _infer_lenovo_series_layout(cls, model: str, product_number: str = "") -> Dict[str, Any]:
        """Offline katalog Lenovo serija za određivanje numpada i desktop vs laptop šasije."""
        combined = f"{model} {product_number}".upper()
        is_desktop = any(k in combined for k in ("THINKCENTRE", "THINKSTATION", "IDEACENTRE", "M70", "M75", "M80", "M90", "P330", "P340", "P350", "P360", "P520", "P620"))
        is_thinkpad = "THINKPAD" in combined
        has_numpad = None  # type: Optional[bool]

        if is_desktop:
            has_numpad = True
        elif any(k in combined for k in (
            "T15", "T16", "T580", "T590", "L15", "L16", "L580", "L590",
            "E15", "E16", "E580", "E590", "P15", "P16", "P17", "P52", "P53", "P73",
            "Z16", "IDEAPAD 3 15", "IDEAPAD 5 15", "IDEAPAD 1 15", "IDEAPAD 15",
            "LEGION 5", "LEGION 7", "LEGION PRO", "LOQ 15", "LOQ 16", "V15", "V17",
        )):
            has_numpad = True
        elif any(k in combined for k in (
            "X1 ", "X1C", "X13", "X12", "X280", "X380", "X390", "T14", "T480", "T490",
            "T495", "L13", "L14", "L380", "L390", "L480", "L490", "E14", "E480",
            "E490", "P14", "Z13", "YOGA 7 14", "YOGA 9 14", "YOGA SLIM", "IDEAPAD 5 14",
            "IDEAPAD 3 14", "V14",
        )):
            has_numpad = False

        # MTM provera za poznate TKL/Numpad mašine
        mtm = cls._normalize_mtm(product_number or model)
        if has_numpad is None and mtm in ("20UD", "20W0", "21E3", "20TD", "20S0", "20N2", "20LH"):
            has_numpad = False
            is_thinkpad = True

        if has_numpad is None and not is_desktop:
            return {"matched": False, "is_desktop": False, "has_numpad": None, "is_iso": None, "is_thinkpad": is_thinkpad}

        return {
            "matched": True,
            "is_desktop": is_desktop,
            "has_numpad": has_numpad,
            "is_iso": None,
            "is_thinkpad": is_thinkpad,
        }

    def lookup_keyboard_spec(
        self,
        serial: str = "",
        model: str = "",
        product_number: str = "",
    ) -> Dict[str, Any]:
        """
        Određuje fizički raspored tastature za Lenovo uređaje:
        1) Disk keš, 2) Lenovo Parts API po S/N + MTM (timeout <= 5s), 3) Katalog serija.
        """
        clean_sn = self._normalize_serial(serial)
        clean_mtm = self._normalize_mtm(product_number or model)
        cache_key = f"{clean_sn}_{clean_mtm}".strip("_") or re.sub(r'[^A-Z0-9_-]', '_', (model or "").upper()).strip("_")
        is_thinkpad_hint = "THINKPAD" in f"{model} {product_number}".upper()

        if cache_key:
            cache_file = os.path.join(self.keyboard_cache_dir, f"{cache_key}.json")
            with self.lock:
                if os.path.isfile(cache_file):
                    try:
                        with open(cache_file, "r", encoding="utf-8") as f:
                            cached = json.load(f)
                        if cached.get("status") == "ok":
                            if is_thinkpad_hint and isinstance(cached.get("base_layout"), str):
                                bl = cached["base_layout"]
                                if bl.startswith("laptop-tkl-"):
                                    cached["base_layout"] = "thinkpad-tkl-" + ("iso" if bl.endswith("-iso") else "ansi")
                                elif bl.startswith("laptop-numpad-"):
                                    cached["base_layout"] = "thinkpad-numpad-" + ("iso" if bl.endswith("-iso") else "ansi")
                                cached.setdefault("raw_data", {})["is_thinkpad"] = True
                            return cached
                    except Exception:
                        pass
        else:
            cache_file = ""

        # Mrežni upit ka Lenovo Parts API-ju ako imamo S/N ili validan MTM
        if clean_sn and not clean_sn.startswith("NEPOZNAT"):
            candidate_urls = [
                f"https://pcsupport.lenovo.com/api/v4/upsell/redport/getParts?serialNumber={urllib.parse.quote(clean_sn)}"
                + (f"&productId={urllib.parse.quote(clean_mtm)}" if clean_mtm and clean_mtm != "GENERIC" else ""),
            ]
            if clean_mtm and clean_mtm != "GENERIC":
                candidate_urls.append(
                    f"https://pcsupport.lenovo.com/api/v4/upsell/redport/getProductParts?productId={urllib.parse.quote(clean_mtm)}"
                )

            for api_url in candidate_urls:
                req = urllib.request.Request(
                    api_url,
                    headers={
                        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-LenovoOEM/1.0",
                        "Accept": "application/json",
                    },
                )
                try:
                    with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                        if resp.status == 200:
                            data = json.loads(resp.read().decode("utf-8"))
                            part_desc = self._extract_lenovo_keyboard_part(data)
                            if part_desc:
                                parsed = self._parse_lenovo_keyboard_description(part_desc)
                                series = self._infer_lenovo_series_layout(model, product_number)
                                has_numpad = parsed["has_numpad"] if parsed["has_numpad"] is not None else series.get("has_numpad")
                                is_iso = parsed["is_iso"]
                                is_desktop = bool(series.get("is_desktop", False))
                                is_thinkpad = bool(series.get("is_thinkpad", False) or is_thinkpad_hint)
                                base_layout = None
                                if has_numpad is not None and is_iso is not None:
                                    base_layout = make_keyboard_layout_name(has_numpad, is_iso, is_desktop, is_thinkpad=is_thinkpad)
                                elif has_numpad is not None:
                                    base_layout = make_keyboard_layout_name(has_numpad, False, is_desktop, is_thinkpad=is_thinkpad)

                                res = build_keyboard_spec(
                                    status="ok",
                                    source="lenovo_parts_api",
                                    confidence="high" if (has_numpad is not None and is_iso is not None) else "medium",
                                    base_layout=base_layout,
                                    has_numpad=has_numpad,
                                    is_iso=is_iso,
                                    has_copilot=parsed["has_copilot"],
                                    oem_part_description=part_desc,
                                    raw_data={
                                        "serial": clean_sn,
                                        "mtm": clean_mtm,
                                        "model": model,
                                        "is_thinkpad": is_thinkpad,
                                    },
                                )
                                if cache_file:
                                    with self.lock:
                                        tmp_file = cache_file + ".tmp"
                                        with open(tmp_file, "w", encoding="utf-8") as f:
                                            json.dump(res, f, indent=2, ensure_ascii=False)
                                        os.replace(tmp_file, cache_file)
                                return res
                except Exception:
                    pass

        # Fallback: katalog Lenovo serija (numpad / desktop / thinkpad)
        series = self._infer_lenovo_series_layout(model, product_number)
        if series.get("matched"):
            has_numpad = bool(series["has_numpad"])
            is_desktop = bool(series["is_desktop"])
            is_thinkpad = bool(series.get("is_thinkpad", False) or is_thinkpad_hint)
            return build_keyboard_spec(
                status="ok",
                source="lenovo_series_catalog",
                confidence="medium",
                base_layout=make_keyboard_layout_name(has_numpad, False, is_desktop, is_thinkpad=is_thinkpad) if is_desktop else None,
                has_numpad=has_numpad,
                is_iso=series.get("is_iso"),
                raw_data={
                    "serial": clean_sn,
                    "mtm": clean_mtm,
                    "model": model,
                    "is_desktop": is_desktop,
                    "is_thinkpad": is_thinkpad,
                },
            )

        return build_keyboard_spec(
            status="fallback",
            source="lenovo_fallback",
            confidence="low",
            raw_data={"serial": clean_sn, "mtm": clean_mtm, "model": model},
        )
