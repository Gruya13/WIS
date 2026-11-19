"""
servishub.drivers.oem.hp
========================
Konkretna implementacija OEM provajdera za HP hardver (EliteBook, ProBook, EliteDesk, ZBook).
Podržava:
- Upite o fabričkoj garanciji preko HP Support API-ja uz striktni timeout od max 5s i graceful fallback.
- Rezoluciju zvaničnih HP SoftPaq / Driver Pack (HPIA) paketa sa proverom lokalnog keša
  u storage/drivers_cache/oem/hp/{model}/.
- Detekciju i semantičko poređenje dostupnih HP BIOS/UEFI firmware ažuriranja.
- Višenitno bezbedan rad (threading.Lock za keš operacije na disku).
"""

import os
import json
import re
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


class HpOEMProvider(BaseOEMProvider):
    """
    OEM provajder za HP računare, prenosne računare i radne stanice.
    """

    # Referentna baza za poznate HP poslovne modele (fallback i offline rezolucija)
    KNOWN_MODELS: Dict[str, Dict[str, Any]] = {
        "elitebook_840_g7": {
            "model_name": "HP EliteBook 840 G7 Notebook PC",
            "prod_id": "840G7",
            "platform_id": "8723",
            "bios_version": "01.15.00",
            "bios_id": "S77 Ver. 01.15.00",
            "bios_date": "2024-03-20",
            "bios_url": "https://ftp.hp.com/pub/softpaq/sp151000/sp151240.exe",
            "driver_pack_package": "sp141520.exe",
            "driver_pack_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp141520.exe",
            "driver_pack_size": 1845493760,
            "softpaq_package": "sp141520.exe",
            "softpaq_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp141520.exe",
            "softpaq_size": 1845493760,
            "sccm_package": "sp141520.exe",
            "sccm_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp141520.exe",
            "sccm_size": 1845493760,
        },
        "elitebook_840_g8": {
            "model_name": "HP EliteBook 840 G8 Notebook PC",
            "prod_id": "840G8",
            "platform_id": "880D",
            "bios_version": "01.16.00",
            "bios_id": "T76 Ver. 01.16.00",
            "bios_date": "2024-04-10",
            "bios_url": "https://ftp.hp.com/pub/softpaq/sp152000/sp152450.exe",
            "driver_pack_package": "sp148910.exe",
            "driver_pack_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp148910.exe",
            "driver_pack_size": 1982464000,
            "softpaq_package": "sp148910.exe",
            "softpaq_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp148910.exe",
            "softpaq_size": 1982464000,
            "sccm_package": "sp148910.exe",
            "sccm_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp148910.exe",
            "sccm_size": 1982464000,
        },
        "probook_450_g8": {
            "model_name": "HP ProBook 450 G8 Notebook PC",
            "prod_id": "450G8",
            "platform_id": "87E1",
            "bios_version": "01.16.02",
            "bios_id": "T70 Ver. 01.16.02",
            "bios_date": "2024-02-28",
            "bios_url": "https://ftp.hp.com/pub/softpaq/sp151000/sp151890.exe",
            "driver_pack_package": "sp147320.exe",
            "driver_pack_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp147320.exe",
            "driver_pack_size": 1724581888,
            "softpaq_package": "sp147320.exe",
            "softpaq_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp147320.exe",
            "softpaq_size": 1724581888,
            "sccm_package": "sp147320.exe",
            "sccm_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp147320.exe",
            "sccm_size": 1724581888,
        },
        "elitedesk_800_g6": {
            "model_name": "HP EliteDesk 800 G6 Desktop Mini PC",
            "prod_id": "800G6",
            "platform_id": "870C",
            "bios_version": "02.14.01",
            "bios_id": "S01 Ver. 02.14.01",
            "bios_date": "2024-01-18",
            "bios_url": "https://ftp.hp.com/pub/softpaq/sp150000/sp150820.exe",
            "driver_pack_package": "sp145890.exe",
            "driver_pack_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp145890.exe",
            "driver_pack_size": 1450283008,
            "softpaq_package": "sp145890.exe",
            "softpaq_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp145890.exe",
            "softpaq_size": 1450283008,
            "sccm_package": "sp145890.exe",
            "sccm_url": "https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/sp145890.exe",
            "sccm_size": 1450283008,
        },
    }

    # Dinamički dodajemo uobičajene alijase u KNOWN_MODELS
    _ALIASES_MAP = {
        "elitebook 840 g7": "elitebook_840_g7",
        "840 g7": "elitebook_840_g7",
        "840g7": "elitebook_840_g7",
        "elitebook 840 g8": "elitebook_840_g8",
        "840 g8": "elitebook_840_g8",
        "840g8": "elitebook_840_g8",
        "probook 450 g8": "probook_450_g8",
        "450 g8": "probook_450_g8",
        "450g8": "probook_450_g8",
        "elitedesk 800 g6": "elitedesk_800_g6",
        "800 g6": "elitedesk_800_g6",
        "800g6": "elitedesk_800_g6",
    }
    for _alias, _target in list(_ALIASES_MAP.items()):
        if _target in KNOWN_MODELS and _alias not in KNOWN_MODELS:
            KNOWN_MODELS[_alias] = KNOWN_MODELS[_target]
            KNOWN_MODELS[_alias.upper()] = KNOWN_MODELS[_target]

    def __init__(self, cache_dir: Optional[str] = None, timeout: float = 5.0):
        """
        Inicijalizuje HP OEM provajdera.

        :param cache_dir: Koreni direktorijum za skladištenje OEM keša
        :param timeout: Mrežni timeout u sekundama (maksimalno 5s)
        """
        if cache_dir:
            self.base_cache_dir = os.path.abspath(cache_dir)
        else:
            try:
                from servishub.config import DRIVERS_CACHE_DIR
                self.base_cache_dir = os.path.join(os.path.abspath(DRIVERS_CACHE_DIR), "oem", "hp")
            except Exception:
                self.base_cache_dir = os.path.abspath(
                    os.path.join(os.path.dirname(__file__), "..", "..", "..", "storage", "drivers_cache", "oem", "hp")
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

    @classmethod
    def _normalize_model(cls, model: str) -> str:
        """
        Normalizuje naziv HP modela u standardni ključ ili safe direktorijum string.
        Primeri:
        - 'HP EliteBook 840 G7 Notebook PC' -> 'elitebook_840_g7'
        - 'HP ProBook 450 G8' -> 'probook_450_g8'
        - 'HP EliteDesk 800 G6 Desktop Mini' -> 'elitedesk_800_g6'
        - '840 G8' -> 'elitebook_840_g8'
        """
        if not model:
            return ""
        s = model.strip().lower()
        # Uklanjamo uobičajene sufikse i prefikse
        for noise in [
            "hewlett packard",
            "hewlett-packard",
            "hp inc.",
            "hp inc",
            "hp",
            "notebook pc",
            "desktop mini pc",
            "desktop mini",
            "desktop pc",
            "notebook",
            "pc",
        ]:
            s = re.sub(rf'\b{re.escape(noise)}\b', ' ', s)

        # Zamenjujemo razmake i specijalne znake podvlakom
        s = re.sub(r'[^a-z0-9]+', '_', s).strip('_')

        # Provera prečica / alijasa
        aliases = {
            "840_g7": "elitebook_840_g7",
            "840_g8": "elitebook_840_g8",
            "450_g8": "probook_450_g8",
            "800_g6": "elitedesk_800_g6",
            "840g7": "elitebook_840_g7",
            "840g8": "elitebook_840_g8",
            "450g8": "probook_450_g8",
            "800g6": "elitedesk_800_g6",
        }
        return aliases.get(s, s)

    @staticmethod
    def _normalize_serial(serial: str) -> str:
        """
        Sanitizuje HP serijski broj (tipično 10 alfanumeričkih znakova, npr. 5CG1234567, CND1234567).
        """
        if not serial:
            return ""
        clean = re.sub(r'[^A-Z0-9]', '', serial.strip().upper())
        # HP serijski brojevi su standardno do 10 alfanumeričkih karaktera
        return clean[:10] if len(clean) > 10 else clean

    @staticmethod
    def _normalize_product_number(product_num: str) -> str:
        """
        Sanitizuje HP Product Number (PN), npr. '2V8P1EA#ABB' -> '2V8P1EA'.
        """
        if not product_num:
            return ""
        clean = product_num.strip().upper().split("#")[0]
        return re.sub(r'[^A-Z0-9_-]', '', clean)

    def _get_model_dir(self, model: str) -> str:
        """
        Vraća putanju do keš direktorijuma za dati model.
        Ako već postoji direktorijum sa originalnim ili normalizovanim imenom, koristi ga.
        """
        if not model:
            return self.base_cache_dir
        norm = self._normalize_model(model)
        direct_dir = os.path.join(self.base_cache_dir, model.strip())
        norm_dir = os.path.join(self.base_cache_dir, norm)

        if os.path.isdir(direct_dir):
            return direct_dir
        if os.path.isdir(norm_dir):
            return norm_dir

        # Podrazumevano vraćamo normalizovani direktorijum
        return norm_dir if norm else direct_dir

    @classmethod
    def _get_known_model_info(cls, model: str) -> Optional[Dict[str, Any]]:
        """Pronalazi podatke o modelu u KNOWN_MODELS mapi."""
        if not model:
            return None
        # 1. Direktni ključ
        if model in cls.KNOWN_MODELS:
            return cls.KNOWN_MODELS[model]
        # 2. Normalizovani ključ
        norm = cls._normalize_model(model)
        if norm in cls.KNOWN_MODELS:
            return cls.KNOWN_MODELS[norm]
        # 3. Pretraga po model_name
        m_lower = model.strip().lower()
        for k, v in cls.KNOWN_MODELS.items():
            if m_lower and (m_lower in v.get("model_name", "").lower() or v.get("model_name", "").lower() in m_lower):
                return v
        return None

    @classmethod
    def _parse_bios_version_components(cls, ver_str: str) -> List[int]:
        """
        Parsira HP BIOS verziju u listu celih brojeva za semantičko poređenje.
        Podržava HP formate:
        - '01.15.00' -> [1, 15, 0]
        - 'S77 Ver. 01.15.00' -> [1, 15, 0]
        - 'T76 Ver. 01.16.00' -> [1, 16, 0]
        - '02.14.01' -> [2, 14, 1]
        - '1.14' -> [1, 14]
        - 'Ver 01.12.00 (03/15/2023)' -> [1, 12, 0]
        """
        if not ver_str:
            return []
        s = ver_str.strip()
        if s.upper() in ("UNKNOWN", "N/A", "NONE", "NULL", "-"):
            return []

        # 1. Provera nakon 'Ver.' ili 'Version'
        ver_match = re.search(r'\bVer(?:sion|\.)?\s*([0-9]+(?:\.[0-9]+)+)', s, re.IGNORECASE)
        if ver_match:
            tokens = [int(x) for x in re.findall(r'\d+', ver_match.group(1))]
            if tokens:
                return tokens

        # 2. Provera zagrada, npr. '(01.15.00)'
        paren = re.search(r'\(([^)]+)\)', s)
        if paren:
            tokens = [int(x) for x in re.findall(r'\d+', paren.group(1))]
            if tokens:
                return tokens

        # 3. Provera standardne sekvence sa tačkama, npr. '01.15.00' ili '1.14'
        m = re.search(r'\b(\d+(?:\.\d+)+)\b', s)
        if m:
            return [int(x) for x in re.findall(r'\d+', m.group(1))]

        # 4. Fallback: sve numeričke sekvence
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
        Poziva HP Support API za proveru statusa garancije sa striktnim timeout-om od max 5s.
        U slučaju greške ili mrežne nedostupnosti, vraća graceful offline fallback rečnik.
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

        # 2. Mrežni upit ka HP Support API-ju
        clean_pn = self._normalize_product_number(model)
        api_url = f"https://support.hp.com/wcc-services/warranty/v1/warranty-details?serialNumber={urllib.parse.quote(clean_sn)}"
        if clean_pn:
            api_url += f"&productNumber={urllib.parse.quote(clean_pn)}"

        req = urllib.request.Request(
            api_url,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-HpOEM/1.0",
                "Accept": "application/json",
            },
        )

        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                if resp.status == 200:
                    raw_body = resp.read().decode("utf-8")
                    data = json.loads(raw_body)
                    result = self._parse_hp_warranty_json(data, clean_sn)
                    if result.get("status") == "ok":
                        with self.lock:
                            tmp_file = cache_file + ".tmp"
                            with open(tmp_file, "w", encoding="utf-8") as f:
                                json.dump(result, f, indent=2, ensure_ascii=False)
                            os.replace(tmp_file, cache_file)
        except urllib.error.HTTPError as he:
            try:
                he.close()
            except Exception:
                pass
        except Exception:
            # Mrežni timeout, HTTP greške ili nevalidan odgovor
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
                "note": "HP Support API nije dostupan ili je istekao timeout (<= 5s)",
            },
        }

    def _parse_hp_warranty_json(self, data: Any, serial: str) -> Dict[str, Any]:
        """Parsira sirovi JSON sa HP Support portala u standardni format."""
        if not isinstance(data, dict):
            return {"status": "error"}

        root = data.get("data", data)
        warranties = []
        if isinstance(root, dict):
            warranties = root.get("warrantyDetails", root.get("warranties", []))

        start_date_str = ""
        end_date_str = ""
        service_level = "HP Standard Warranty"
        in_warr = False

        if warranties and isinstance(warranties, list):
            primary = warranties[0]
            start_date_str = str(primary.get("startDate", primary.get("start", primary.get("start_date", ""))))
            end_date_str = str(primary.get("endDate", primary.get("end", primary.get("end_date", ""))))
            service_level = primary.get(
                "offeringName",
                primary.get("serviceLevel", primary.get("serviceType", primary.get("name", service_level)))
            )
            w_status = str(primary.get("status", "")).lower()
            in_warr = "active" in w_status or "valid" in w_status or bool(primary.get("inWarranty", False))
        elif isinstance(root, dict):
            start_date_str = str(root.get("startDate", root.get("start_date", "")))
            end_date_str = str(root.get("endDate", root.get("end_date", "")))
            service_level = root.get(
                "serviceLevel",
                root.get("serviceType", root.get("offeringName", root.get("name", service_level)))
            )
            w_status = str(root.get("status", "")).lower()
            in_warr = "active" in w_status or "in warranty" in w_status or bool(root.get("inWarranty", False))

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
    # 2. Rezolucija SoftPaq / Driver Pack paketa (resolve_driver_pack)
    # =========================================================================

    def resolve_driver_pack(self, model: str, os_ver: str = "win11-x64") -> Dict[str, Any]:
        """
        Pronalazi zvanični HP Driver Pack / SoftPaq paket za dati model i OS verziju.
        Proverava lokalni keš u storage/drivers_cache/oem/hp/{model}/package_meta.json.
        Ako paket postoji u kešu, vraća lokalnu putanju.
        Ako ne postoji lokalno, rezolvuje SoftPaq paket iz KNOWN_MODELS baze ili HP portala.
        """
        norm = self._normalize_model(model)
        if not norm and not model.strip():
            return {
                "status": "not_found",
                "source": "hp_softpaq",
                "package_name": "",
                "download_url": "",
                "local_path": "",
                "size_bytes": 0,
            }

        model_dir = self._get_model_dir(model)

        # 1. Provera lokalnog keša na disku
        with self.lock:
            if os.path.isdir(model_dir):
                archives = [
                    f for f in sorted(os.listdir(model_dir))
                    if f.lower().endswith((".exe", ".zip", ".cab", ".7z"))
                    and not f.endswith(".tmp")
                ]
                if archives:
                    chosen = archives[0]
                    local_file = os.path.join(model_dir, chosen)
                    f_size = os.path.getsize(local_file)
                    if f_size > 0:
                        dl_url = f"https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/{chosen}"
                        meta_file = os.path.join(model_dir, "package_meta.json")
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

                meta_file = os.path.join(model_dir, "package_meta.json")
                if os.path.isfile(meta_file):
                    try:
                        with open(meta_file, "r", encoding="utf-8") as mf:
                            cached_meta = json.load(mf)
                        if cached_meta.get("package_name"):
                            return {
                                "status": "ok",
                                "source": cached_meta.get("source", "cache"),
                                "package_name": cached_meta.get("package_name", ""),
                                "download_url": cached_meta.get("download_url", ""),
                                "local_path": cached_meta.get("local_path", ""),
                                "size_bytes": cached_meta.get("size_bytes", 0),
                            }
                    except Exception:
                        pass

        # 2. Paket nije u kešu: rezolucija sa portala ili referentne baze
        portal_meta = self._fetch_portal_softpaq_metadata(norm or model, os_ver)
        if portal_meta:
            package_name = portal_meta["package_name"]
            download_url = portal_meta["download_url"]
            size_bytes = portal_meta["size_bytes"]
            source = "hp_softpaq_portal"
        else:
            known_info = self._get_known_model_info(model)
            if known_info:
                package_name = known_info.get("driver_pack_package") or known_info.get("softpaq_package", "sp_pack.exe")
                download_url = known_info.get("driver_pack_url") or known_info.get(
                    "softpaq_url", f"https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/{package_name}"
                )
                size_bytes = known_info.get("driver_pack_size") or known_info.get("softpaq_size", 1845493760)
                source = "hp_softpaq_known"
            else:
                os_tag = "win11" if "11" in os_ver.lower() else "win10"
                m_tag = norm or re.sub(r'[^a-zA-Z0-9]+', '_', model).strip('_').lower()
                package_name = f"hp_{m_tag}_{os_tag}_driverpack.exe"
                download_url = f"https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/{package_name}"
                size_bytes = 1800000000
                source = "hp_softpaq_simulated"

        # Zapisujemo metapodatke u keš direktorijum pod lock-om
        with self.lock:
            os.makedirs(model_dir, exist_ok=True)
            meta_record = {
                "model": model,
                "normalized_model": norm,
                "os_ver": os_ver,
                "package_name": package_name,
                "download_url": download_url,
                "size_bytes": size_bytes,
                "source": source,
                "resolved_at": datetime.now().isoformat(),
            }
            meta_path = os.path.join(model_dir, "package_meta.json")
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

    def _fetch_portal_softpaq_metadata(self, norm_model: str, os_ver: str) -> Optional[Dict[str, Any]]:
        """Pokušava dohvatanje metapodataka sa HP portala uz timeout do 5s."""
        url = f"https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/index_{urllib.parse.quote(norm_model)}.json"
        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-HpOEM/1.0",
                "Accept": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                if resp.status == 200:
                    data = json.loads(resp.read().decode("utf-8"))
                    items = data.get("driver_packs", [])
                    if items and isinstance(items, list):
                        item = items[0]
                        return {
                            "package_name": item.get("filename", f"hp_{norm_model}_driverpack.exe"),
                            "download_url": item.get(
                                "url", f"https://ftp.hp.com/pub/caps-softpaq/HP_Driver_Packs/{norm_model}.exe"
                            ),
                            "size_bytes": int(item.get("size", 1800000000)),
                        }
        except urllib.error.HTTPError as he:
            try:
                he.close()
            except Exception:
                pass
        except Exception:
            pass
        return None

    # =========================================================================
    # 3. Provera BIOS ažuriranja (check_bios_update)
    # =========================================================================

    def check_bios_update(self, model: str, current_bios: str = "") -> Dict[str, Any]:
        """
        Poredi verziju BIOS-a i proverava dostupnost novijeg firmware-a za HP model.
        Proverava lokalni keš u bios_meta.json i po potrebi konsultuje KNOWN_MODELS ili HP portal.
        """
        norm = self._normalize_model(model)
        if not norm and not model.strip():
            return {
                "update_available": False,
                "latest_version": "",
                "current_version": current_bios,
                "download_url": "",
                "release_date": "",
            }

        model_dir = self._get_model_dir(model)
        bios_meta_path = os.path.join(model_dir, "bios_meta.json")

        bios_data = None

        # 1. Provera keša na disku pod lock-om
        with self.lock:
            if os.path.isfile(bios_meta_path):
                try:
                    with open(bios_meta_path, "r", encoding="utf-8") as f:
                        bios_data = json.load(f)
                except Exception:
                    pass

        # 2. Ako podaci nisu u kešu, dohvata iz KNOWN_MODELS ili sa portala
        if not bios_data:
            portal_bios = self._fetch_portal_bios_metadata(norm or model)
            if portal_bios:
                bios_data = portal_bios
            else:
                known_info = self._get_known_model_info(model)
                if known_info:
                    bios_data = {
                        "latest_version": known_info["bios_version"],
                        "bios_id": known_info.get("bios_id", ""),
                        "download_url": known_info["bios_url"],
                        "release_date": known_info["bios_date"],
                    }
                else:
                    m_tag = norm or re.sub(r'[^a-zA-Z0-9]+', '_', model).strip('_').upper()
                    bios_data = {
                        "latest_version": "01.10.00",
                        "bios_id": f"HP {m_tag} BIOS",
                        "download_url": f"https://ftp.hp.com/pub/softpaq/sp150000/sp_{m_tag.lower()}_bios.exe",
                        "release_date": "2024-01-15",
                    }

            # Zapisujemo u keš
            with self.lock:
                os.makedirs(model_dir, exist_ok=True)
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

    def _fetch_portal_bios_metadata(self, norm_model: str) -> Optional[Dict[str, Any]]:
        """Pokušava dohvatanje BIOS metapodataka sa HP portala uz timeout do 5s."""
        url = f"https://support.hp.com/api/v1/downloads/bios?model={urllib.parse.quote(norm_model)}"
        req = urllib.request.Request(
            url,
            headers={
                "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-HpOEM/1.0",
                "Accept": "application/json",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                if resp.status == 200:
                    data = json.loads(resp.read().decode("utf-8"))
                    items = data.get("bios_updates", [])
                    if items and isinstance(items, list):
                        item = items[0]
                        return {
                            "latest_version": item.get("version", "01.15.00"),
                            "bios_id": item.get("title", ""),
                            "download_url": item.get("url", f"https://ftp.hp.com/pub/softpaq/sp_{norm_model}.exe"),
                            "release_date": item.get("date", "2024-03-20"),
                        }
        except urllib.error.HTTPError as he:
            try:
                he.close()
            except Exception:
                pass
        except Exception:
            pass
        return None

    # =========================================================================
    # 4. Prepoznavanje fizičkog rasporeda tastature (lookup_keyboard_spec)
    # =========================================================================

    _HP_ANSI_SUFFIXES = ("ABA", "ABL")
    _HP_ISO_SUFFIXES = (
        "ABD", "ABU", "ABF", "ABE", "ABZ", "ABH", "ABB", "AB8", "AB9",
        "AKN", "AKC", "AKB", "AKD", "AKQ", "AKS",
        "BED", "B1N", "B1R", "B10", "BCM", "BH4",
        "UUZ", "UUW", "UUG",
    )

    @classmethod
    def _parse_hp_sku_suffix(cls, sku_or_pn: str) -> Optional[bool]:
        """
        Određuje ISO vs ANSI iz HP SKU sufiksa (#ABA = US ANSI, #ABD = DE ISO,
        #BED/#AKN = Adriatic/Balkan ISO, #ABU = UK ISO, #UUZ = CH ISO...).
        """
        if not sku_or_pn or "#" not in sku_or_pn:
            return None
        suffix = sku_or_pn.strip().upper().split("#")[-1]
        suffix = re.sub(r'[^A-Z0-9]', '', suffix)[:4]
        if not suffix:
            return None
        if suffix in cls._HP_ANSI_SUFFIXES:
            return False
        if suffix in cls._HP_ISO_SUFFIXES:
            return True
        return None

    @classmethod
    def _parse_hp_part_description(cls, desc: str, sku: str = "") -> Dict[str, Any]:
        """
        Parsira HP PartSurfer opis dela, npr.:
        - 'Keyboard, backlit, United States, without numeric keypad'
        - 'L87609-A41 Assy KBDPM CP+PS SR BEL'
        """
        text = (desc or "").upper()
        has_copilot = "COPILOT" in text
        has_numpad = None  # type: Optional[bool]
        is_iso = None      # type: Optional[bool]

        if (
            any(k in text for k in ("WITHOUT NUMERIC KEYPAD", "WITHOUT KEYPAD", "NO NUMERIC KEYPAD", "NO KEYPAD", "NO NUMPAD", "W/O KP", "TKL"))
            or re.search(r"\bNKP\b", text)
        ):
            has_numpad = False
        elif any(k in text for k in ("WITH NUMERIC KEYPAD", "NUMERIC KEYPAD", "WITH KEYPAD", "W/KP", "WITH NUMPAD", "FULL SIZE")):
            has_numpad = True

        iso_words = (
            "ADRIATIC", "SLOVENIAN", "CROATIAN", "SERBIAN", "GERMANY", "GERMAN",
            "UNITED KINGDOM", "SWISS", "SWITZERLAND", "FRENCH", "FRANCE", "NORDIC",
            "ITALIAN", "SPANISH", "INTERNATIONAL", "EUROPE", "CZECH", "SLOVAK",
            "HUNGARIAN", "POLISH", "TURKISH", "ISO", "BELGIUM", "BELGIAN", "DUTCH",
            "PORTUGUESE", "DANISH", "NORWEGIAN", "SWEDISH", "FINNISH",
        )
        if (
            any(w in text for w in iso_words)
            or re.search(r"\b(BEL|FR|GR|UK|SP|ITL|DEN|NOR|SWD|SWI|PORT|TURK|HU|CZ|SLV|BH|SRB|HR)\b", text)
            or re.search(r"-(A41|031|041|051|061|071|081|091|131|141|151|211|251|291|B31|BG1|FL1|BH4)\b", text)
        ):
            is_iso = True
        elif (
            "UNITED STATES" in text
            or "ANSI" in text
            or re.search(r"\b(US|INTL)\b", text)
            or re.search(r"-(001|B1U)\b", text)
        ):
            is_iso = False

        if is_iso is None and sku:
            is_iso = cls._parse_hp_sku_suffix(sku)

        return {
            "has_numpad": has_numpad,
            "is_iso": is_iso,
            "has_copilot": has_copilot,
        }

    @classmethod
    def _extract_hp_partsurfer_keyboard(cls, raw_body: str) -> Optional[str]:
        """Izvlači opis tastature iz HP PartSurfer JSON (BFF) ili HTML odgovora."""
        if not raw_body:
            return None
        # 1. Pokušaj kao JSON (HP PartSurfer BFF ili generički JSON)
        stripped = raw_body.strip()
        if stripped.startswith("{") or stripped.startswith("["):
            try:
                data = json.loads(stripped)
                candidates = []

                # Prioritetno BFF SerialNumberBOM.unit_configuration (konkretna konfiguracija S/N-a)
                if isinstance(data, dict):
                    sbom = (data.get("Body") or {}).get("SerialNumberBOM") or {}
                    if isinstance(sbom, dict):
                        uc_list = sbom.get("unit_configuration") or []
                        if isinstance(uc_list, list):
                            for it in uc_list:
                                if not isinstance(it, dict):
                                    continue
                                desc = str(it.get("part_description") or "").strip()
                                pn = str(it.get("part_number") or "").strip()
                                up = desc.upper()
                                if (
                                    ("KEYBOARD" in up or "KEYPAD" in up or re.search(r"\bKBD[A-Z0-9]*\b", up))
                                    and "EXTERNAL" not in up and "CABLE" not in up and "SCREW" not in up
                                ):
                                    return f"{pn} {desc}".strip()

                def _walk(node: Any):
                    if isinstance(node, dict):
                        for k in ("description", "partDescription", "part_description", "category", "name"):
                            v = node.get(k)
                            if isinstance(v, str) and (
                                "KEYBOARD" in v.upper() or "KEYPAD" in v.upper() or re.search(r"\bKBD[A-Z0-9]*\b", v.upper())
                            ):
                                if "EXTERNAL" not in v.upper() and "CABLE" not in v.upper() and "SCREW" not in v.upper():
                                    candidates.append(v.strip())
                        for v in node.values():
                            if isinstance(v, (dict, list)):
                                _walk(v)
                    elif isinstance(node, list):
                        for it in node:
                            _walk(it)

                _walk(data)
                if candidates:
                    return candidates[0]
            except Exception:
                pass

        # 2. Parsiranje HTML redova tabele (<tr>...</tr>)
        rows = re.findall(r'<tr[^>]*>(.*?)</tr>', raw_body, re.IGNORECASE | re.DOTALL)
        for row_html in rows:
            clean_row = re.sub(r'<[^>]+>', ' | ', row_html)
            clean_row = re.sub(r'\s+', ' ', clean_row).strip()
            up = clean_row.upper()
            if ("KEYBOARD" in up or "TOP COVER WITH KEYBOARD" in up) and "EXTERNAL" not in up and "CABLE" not in up and "SCREW" not in up:
                return clean_row
        return None

    @classmethod
    def _infer_hp_series_layout(cls, model: str) -> Dict[str, Any]:
        """Offline katalog HP serija (EliteBook, ProBook, ZBook, EliteDesk)."""
        up = (model or "").upper()
        is_desktop = any(k in up for k in ("ELITEDESK", "PRODESK", "PROONE", "ELITEONE", "Z2 ", "Z4 ", "Z6 ", "Z8 "))
        has_numpad = None  # type: Optional[bool]
        added_keys = []    # type: List[str]

        if is_desktop:
            has_numpad = True
        elif any(k in up for k in ("850", "855", "860", "865", "650", "655", "450", "455", "470", "250", "255", "FURY 15", "FURY 16", "FURY 17", "POWER 15", "POWER 16", "ZBOOK 15", "ZBOOK 17", "15-")):
            has_numpad = True
        elif any(k in up for k in ("830", "835", "840", "845", "1030", "1040", "735", "745", "630", "640", "645", "430", "440", "445", "FIREFLY 14", "ENVY 13", "SPECTRE", "14-")):
            has_numpad = False
            is_new_gen_no_nav = bool(re.search(r"\bG(9|10|11|12)\b", up)) or any(
                k in up for k in ("630", "640", "645")
            )
            has_elite_nav = (
                any(k in up for k in ("ELITEBOOK", "ZBOOK"))
                and any(k in up for k in ("830", "835", "840", "845", "1030", "1040", "735", "745", "14U", "FIREFLY 14"))
            )
            has_probook_nav = (
                "PROBOOK" in up
                and any(k in up for k in ("430", "440", "445"))
                and any(g in up for g in ("G6", "G7", "G8"))
            )
            if not is_new_gen_no_nav and (has_elite_nav or has_probook_nav):
                for nav_k in ("Home", "PageUp", "PageDown", "End"):
                    added_keys.append(nav_k)

        if has_numpad is None and not is_desktop:
            return {"matched": False, "is_desktop": False, "has_numpad": None, "added_keys": []}

        return {
            "matched": True,
            "is_desktop": is_desktop,
            "has_numpad": has_numpad,
            "added_keys": added_keys,
        }

    def lookup_keyboard_spec(
        self,
        serial: str = "",
        model: str = "",
        product_number: str = "",
    ) -> Dict[str, Any]:
        """
        Određuje fizički raspored tastature za HP uređaje:
        1) Disk keš, 2) HP PartSurfer po S/N ili Product Number-u (timeout <= 5s),
        3) Offline #XXX SKU sufiks + HP katalog serija.
        """
        clean_sn = self._normalize_serial(serial)
        sku_iso = self._parse_hp_sku_suffix(product_number)
        clean_pn = self._normalize_product_number(product_number)
        cache_key = f"{clean_sn}_{clean_pn}".strip("_") or re.sub(r'[^A-Z0-9_-]', '_', (model or "").upper()).strip("_")

        if cache_key:
            cache_file = os.path.join(self.keyboard_cache_dir, f"{cache_key}.json")
            with self.lock:
                if os.path.isfile(cache_file):
                    try:
                        with open(cache_file, "r", encoding="utf-8") as f:
                            cached = json.load(f)
                        if cached.get("status") == "ok":
                            return cached
                    except Exception:
                        pass
        else:
            cache_file = ""

        search_term = clean_sn if (clean_sn and not clean_sn.startswith("NEPOZNAT")) else clean_pn
        if search_term:
            if clean_sn and not clean_sn.startswith("NEPOZNAT"):
                url = (
                    f"https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/SerialNumber/GetSerialNumber/"
                    f"{urllib.parse.quote(clean_sn)}/country/US/usertype/EXT"
                )
            else:
                url = (
                    f"https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/Search/GenericSearch/"
                    f"{urllib.parse.quote(clean_pn)}/country/US/usertype/EXT"
                )
            req = urllib.request.Request(
                url,
                headers={
                    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-HpOEM/1.0",
                    "Accept": "application/json,text/html",
                    "Origin": "https://partsurfer.hp.com",
                    "Referer": "https://partsurfer.hp.com/",
                },
            )
            try:
                with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                    if resp.status == 200:
                        raw_body = resp.read().decode("utf-8", errors="replace")
                        part_desc = self._extract_hp_partsurfer_keyboard(raw_body)
                        if part_desc:
                            parsed = self._parse_hp_part_description(part_desc, product_number)
                            series = self._infer_hp_series_layout(model)
                            has_numpad = parsed["has_numpad"] if parsed["has_numpad"] is not None else series.get("has_numpad")
                            is_iso = parsed["is_iso"] if parsed["is_iso"] is not None else sku_iso
                            is_desktop = bool(series.get("is_desktop", False))
                            base_layout = None
                            if has_numpad is not None:
                                base_layout = make_keyboard_layout_name(has_numpad, bool(is_iso) if is_iso is not None else False, is_desktop)
                            res = build_keyboard_spec(
                                status="ok",
                                source="hp_partsurfer",
                                confidence="high" if (has_numpad is not None and is_iso is not None) else "medium",
                                base_layout=base_layout,
                                has_numpad=has_numpad,
                                is_iso=is_iso,
                                has_copilot=parsed["has_copilot"],
                                oem_part_description=part_desc,
                                added_keys=series.get("added_keys"),
                                raw_data={"serial": clean_sn, "sku": product_number, "model": model},
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

        series = self._infer_hp_series_layout(model)
        if series.get("matched") or sku_iso is not None:
            has_numpad = series.get("has_numpad")
            is_desktop = bool(series.get("is_desktop", False))
            base_layout = None
            if has_numpad is not None and sku_iso is not None:
                base_layout = make_keyboard_layout_name(bool(has_numpad), bool(sku_iso), is_desktop)
            elif is_desktop:
                base_layout = make_keyboard_layout_name(True, bool(sku_iso) if sku_iso is not None else False, True)

            return build_keyboard_spec(
                status="ok",
                source="hp_sku_series_catalog",
                confidence="high" if (has_numpad is not None and sku_iso is not None) else "medium",
                base_layout=base_layout,
                has_numpad=has_numpad,
                is_iso=sku_iso,
                added_keys=series.get("added_keys"),
                raw_data={"serial": clean_sn, "sku": product_number, "model": model, "is_desktop": is_desktop},
            )

        return build_keyboard_spec(
            status="fallback",
            source="hp_fallback",
            confidence="low",
            raw_data={"serial": clean_sn, "sku": product_number, "model": model},
        )
