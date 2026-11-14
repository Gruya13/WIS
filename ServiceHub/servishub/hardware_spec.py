"""
servishub.hardware_spec
=======================
Centralni koordinator za određivanje fabričke konfiguracije uređaja (OEM BOM lookup):
- Touchscreen (Touch vs Non-Touch panel)
- Čitač otiska prsta (Fingerprint / Biometric)
- Web kamera i IR kamera
- Touchpad, Zvučnici, Mikrofon

Koristi:
1) Lokalni keš u storage/hw_spec_cache/
2) Zvanični OEM Parts / BOM API po serijskom broju / SKU (HP PartSurfer BFF, Lenovo Parts, Dell AssetParts)
3) Offline heuristiku naziva modela i šasije
"""

import json
import logging
import os
import re
import urllib.parse
import urllib.request
from typing import Any, Dict, List, Optional, Tuple

from servishub.config import active_config

logger = logging.getLogger("servishub.hardware_spec")


def _get_cache_dir() -> str:
    cdir = os.path.join(active_config.base_dir, "storage", "hw_spec_cache")
    os.makedirs(cdir, exist_ok=True)
    return cdir


def _detect_vendor(manufacturer: str, model: str, sku: str) -> str:
    combined = f"{manufacturer} {model} {sku}".upper()
    if any(k in combined for k in ("HP", "HEWLETT", "ELITEBOOK", "PROBOOK", "ZBOOK", "ELITEDESK", "PRODESK")):
        return "HP"
    if any(k in combined for k in ("LENOVO", "THINKPAD", "IDEAPAD", "THINKCENTRE", "YOGA", "LEGION")):
        return "Lenovo"
    if any(k in combined for k in ("DELL", "LATITUDE", "OPTIPLEX", "PRECISION", "XPS", "VOSTRO", "INSPIRON")):
        return "Dell"
    return ""


def _http_get_json(url: str, timeout: float = 4.5, headers: Optional[Dict[str, str]] = None) -> Optional[Any]:
    req_headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ServisHub-HwSpec/1.0",
        "Accept": "application/json",
    }
    if headers:
        req_headers.update(headers)
    req = urllib.request.Request(url, headers=req_headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            if resp.status == 200:
                return json.loads(resp.read().decode("utf-8", errors="replace"))
    except Exception:
        pass
    return None


def _analyze_touch_entries(entries: List[Tuple[str, str]], is_primary: bool) -> Tuple[Optional[bool], str]:
    saw_panel = False
    first_panel_desc = ""

    for pn, desc in entries:
        txt = (desc or "").upper()
        if any(skip in txt for skip in ("TOUCHPAD", "TOUCH PAD", "CLICKPAD", "TOUCHCHIP", "TOUCHSTYK")):
            continue

        if not any(kw in txt for kw in ("PNL", "PANEL", "LCD", "DISPLAY", "OLED", "DIGITIZER", "TOUCHSCREEN", "TOUCH SCREEN")):
            continue

        if any(s in txt for s in ("BACK COVER", "BEZEL", "CABLE", "HINGE", "SUPPORT KIT", "BASE ENCLOSURE")):
            if not any(t in txt for t in ("TOUCHSCREEN", "TOUCH SCREEN", "DIGITIZER")):
                continue

        saw_panel = True
        if not first_panel_desc:
            first_panel_desc = f"{pn} {desc}".strip()

        if any(nt in txt for nt in ("NON-TOUCH", "NON TOUCH", "NO TOUCH", "W/O TOUCH", "WITHOUT TOUCH")) or re.search(r"\bNT\b", txt):
            return False, f"Non-touch panel: {desc}"

        if (
            any(t in txt for t in ("TOUCHSCREEN", "TOUCH SCREEN", "DIGITIZER", "ON-CELL", "IN-CELL"))
            or re.search(r"\bTOUCH\b", txt)
            or re.search(r"\b(?:RAW\s+PANEL|PNL|LCD|BV|AG)\s+TS\b", txt)
        ):
            return True, f"{pn} {desc}".strip()

    if is_primary and saw_panel:
        return False, f"UnitConfig panel (non-touch): {first_panel_desc}"
    if not is_primary and saw_panel:
        return False, f"Panel bez Touch oznake: {first_panel_desc}"
    return None, ""


def estimate_usb_port_count(manufacturer: str = "", model: str = "", is_desktop: Optional[bool] = None) -> int:
    """Procena broja fizičkih USB portova (USB-A + USB-C) po modelu/šasiji."""
    if is_desktop:
        return 6
    up = f"{manufacturer} {model}".upper()
    if any(k in up for k in ("ELITEDESK", "PRODESK", "OPTIPLEX", "THINKCENTRE", "PROONE", "ELITEONE")):
        return 6
    if "ELITEBOOK" in up or "ZBOOK" in up:
        if "630" in up:
            return 3
        if any(k in up for k in ("640", "645", "650", "655", "FIREFLY")):
            return 4
        if any(k in up for k in ("830", "835", "840", "845", "850", "855", "860", "865", "1030", "1040")):
            if "G5" in up or "G6" in up:
                return 3
            if re.search(r"\bG(7|8|9|10|11)\b", up):
                return 4
    elif "PROBOOK" in up:
        if any(k in up for k in ("430", "440", "445", "450", "455")) and re.search(r"\bG(8|9|10|11)\b", up):
            return 4
    elif "THINKPAD" in up:
        if any(k in up for k in ("T14", "T480", "T490", "X1 CARBON")):
            return 4
    elif "LATITUDE" in up:
        if any(k in up for k in ("5420", "5430", "5440", "5520", "5530", "5540", "7420", "7430", "7440")):
            return 4
    return 3


def _parse_keyboard_from_bom(
    primary_entries: List[Tuple[str, str]],
    secondary_entries: List[Tuple[str, str]],
) -> Tuple[Optional[bool], Optional[bool], str]:
    """Parsira podatke o tastaturi; ISO/ANSI isključivo iz primary_entries (unit_configuration)."""
    kbd_iso: Optional[bool] = None
    kbd_numpad: Optional[bool] = None
    kbd_desc = ""

    def _scan(entries: List[Tuple[str, str]], allow_iso: bool) -> None:
        nonlocal kbd_iso, kbd_numpad, kbd_desc
        for pn, desc in entries:
            txt = (desc or "").strip()
            if not txt:
                continue
            up = txt.upper()
            pn_up = (pn or "").upper()
            looks_like_kbd = (
                "KEYBOARD" in up
                or "TOP COVER WITH KEYBOARD" in up
                or bool(re.search(r"\bKBD[A-Z0-9]*\b", up))
                or bool(re.search(r"\bKEYB\b", up))
            )
            if not looks_like_kbd:
                continue
            if any(bad in up for bad in ("EXTERNAL", "USB ", "WIRELESS", "DONGLE", "MOUSE", "CABLE", "SCREW", "BRACKET", "MYLAR", "CARDREADER", "KEYBOARD FRAME")):
                continue
            if len(txt) > len(kbd_desc):
                kbd_desc = txt
            if kbd_numpad is None:
                if any(nk in up for nk in ("NO KEYPAD", "WITHOUT KEYPAD", "W/O KP", "NO NUMPAD")) or re.search(r"\bNKP\b", up):
                    kbd_numpad = False
                elif any(wk in up for wk in ("WITH KEYPAD", "W/KP", "NUMPAD", "NUMERIC KEYPAD")):
                    kbd_numpad = True
            if allow_iso and kbd_iso is None:
                if (
                    any(iw in up for iw in ("FRENCH", "BELGIAN", "GERMAN", "SWISS", "NORDIC", "SWEDISH", "FINNISH", "DANISH", "NORWEGIAN", "UNITED KINGDOM", "BRITISH", "SPANISH", "ITALIAN", "PORTUGUESE", "HUNGARIAN", "CZECH", "SLOVAK", "SLOVENIAN", "CROATIAN", "SERBIAN", "POLISH", "TURKISH", "DUTCH", "ADRIATIC", "EURO"))
                    or re.search(r"\b(ISO|BEL|FR|GR|UK|SP|ITL|DEN|NOR|SWD|SWI|PORT|TURK|HU|CZ|SLV|BH|SRB|HR)\b", up)
                    or re.search(r"-(A41|031|041|051|061|071|081|091|131|141|151|211|251|291|B31|BG1|FL1|BH4)\b", pn_up)
                ):
                    kbd_iso = True
                elif (
                    "UNITED STATES" in up
                    or "ENGLISH-US" in up
                    or re.search(r"\b(US|ANSI|INTL)\b", up)
                    or re.search(r"-(001|B1U)\b", pn_up)
                ):
                    kbd_iso = False

    if primary_entries:
        _scan(primary_entries, True)
        if not kbd_desc:
            _scan(secondary_entries, False)
    else:
        _scan(secondary_entries, False)

    return kbd_iso, kbd_numpad, kbd_desc


def _evaluate_bom_entries(
    source: str,
    primary_entries: List[Tuple[str, str]],
    secondary_entries: List[Tuple[str, str]],
    is_desktop: Optional[bool] = None,
) -> Dict[str, Any]:
    total = len(primary_entries) + len(secondary_entries)
    res: Dict[str, Any] = {
        "schema_version": 2,
        "bom_loaded": total > 0,
        "source": source,
        "summary": f"{source} ({total} delova)" if total > 0 else "",
        "has_touchscreen": None,
        "touchscreen_evidence": "",
        "has_fingerprint": None,
        "fingerprint_evidence": "",
        "has_webcam": None,
        "webcam_evidence": "",
        "has_ir_camera": None,
        "ir_camera_evidence": "",
        "has_touchpad": None,
        "has_speakers": None,
        "has_microphone": None,
        "has_keyboard_iso": None,
        "has_keyboard_numpad": None,
        "keyboard_desc": "",
    }
    if total == 0:
        return res

    # 1. Touchscreen
    has_ts, ts_ev = _analyze_touch_entries(primary_entries, True)
    if has_ts is None and secondary_entries:
        has_ts, ts_ev = _analyze_touch_entries(secondary_entries, False)
    if has_ts is None:
        has_ts = False
        ts_ev = f"BOM ({total} delova, bez touch panela)"
    res["has_touchscreen"] = has_ts
    res["touchscreen_evidence"] = ts_ev

    # 2. Fingerprint, Webcam, IR, Touchpad, Audio
    all_entries = primary_entries + secondary_entries
    has_fp = False
    explicit_no_fp = False
    fp_ev = ""
    has_cam = False
    explicit_no_cam = False
    cam_ev = ""
    has_ir = False
    ir_ev = ""

    for pn, desc in all_entries:
        txt = (desc or "").upper()

        if not has_fp and not ("SMART CARD" in txt and "FINGER" not in txt and not re.search(r"\bFPR\b", txt)):
            if any(nf in txt for nf in ("NO FPR", "W/O FPR", "WO/FPR", "NO FINGERPRINT", "WITHOUT FPR")):
                explicit_no_fp = True
                fp_ev = desc
            elif any(f in txt for f in ("FINGERPRINT", "FINGER PRINT", "BIOMETRIC", "TOUCHCHIP", "FP READER", "FP SENSOR")) or re.search(r"\bFPR\b", txt):
                has_fp = True
                fp_ev = f"{pn} {desc}".strip()

        if "SCREW" not in txt and "BRACKET" not in txt:
            if any(nc in txt for nc in ("NO CAM", "NO WEBCAM", "W/O CAM", "WITHOUT CAM")):
                explicit_no_cam = True
                cam_ev = desc
            elif "WEBCAM" in txt or "CAMERA" in txt or re.search(r"\b(?:HD|FHD|5MP|RGB)\s+CAM\b", txt):
                has_cam = True
                if not cam_ev:
                    cam_ev = desc
                if any(ir in txt for ir in ("IR CAM", "INFRARED", "RGB/IR", "RGB+IR")) or re.search(r"\bIR\b", txt):
                    has_ir = True
                    ir_ev = desc

        if any(tp in txt for tp in ("TOUCHPAD", "CLICKPAD", "TRACKPAD")) or re.search(r"\bTPAD\b", txt):
            res["has_touchpad"] = True
        if "SPEAKER" in txt or re.search(r"\bSPKR\b", txt):
            res["has_speakers"] = True
        if "MICROPHONE" in txt or re.search(r"\bDMIC\b", txt) or re.search(r"\bMIC\b", txt):
            res["has_microphone"] = True

    if has_fp:
        res["has_fingerprint"] = True
        res["fingerprint_evidence"] = fp_ev
    elif explicit_no_fp or total >= 5:
        res["has_fingerprint"] = False
        res["fingerprint_evidence"] = fp_ev or f"BOM ({total} delova, bez FP čitača)"

    if has_cam:
        res["has_webcam"] = True
        res["webcam_evidence"] = cam_ev
        res["has_ir_camera"] = has_ir
        res["ir_camera_evidence"] = ir_ev if has_ir else "Standardna RGB kamera (bez IR)"
    elif explicit_no_cam or total >= 8:
        res["has_webcam"] = False
        res["webcam_evidence"] = cam_ev or f"BOM ({total} delova, bez kamere)"
        res["has_ir_camera"] = False

    if has_cam and res["has_microphone"] is None and not is_desktop:
        res["has_microphone"] = True

    kbd_iso, kbd_numpad, kbd_desc = _parse_keyboard_from_bom(primary_entries, secondary_entries)
    res["has_keyboard_iso"] = kbd_iso
    res["has_keyboard_numpad"] = kbd_numpad
    res["keyboard_desc"] = kbd_desc

    return res


def _walk_generic_parts(node: Any, acc: List[Tuple[str, str]]) -> None:
    if isinstance(node, dict):
        cat = str(node.get("category") or node.get("commodity") or node.get("partCategory") or node.get("CategoryName") or "")
        desc = str(
            node.get("partDescription")
            or node.get("itemDescription")
            or node.get("description")
            or node.get("partDesc")
            or node.get("componentName")
            or node.get("name")
            or ""
        ).strip()
        pn = str(node.get("partNumber") or node.get("id") or "").strip()
        if desc and desc != "-N/A-":
            acc.append((pn, f"{cat} {desc}".strip()))
        for v in node.values():
            if isinstance(v, (dict, list)):
                _walk_generic_parts(v, acc)
    elif isinstance(node, list):
        for item in node:
            _walk_generic_parts(item, acc)


def resolve_hardware_spec(
    manufacturer: str = "",
    model: str = "",
    serial: str = "",
    sku: str = "",
    is_desktop: Optional[bool] = None,
) -> Dict[str, Any]:
    vendor = _detect_vendor(manufacturer, model, sku)
    clean_sn = re.sub(r"[^A-Z0-9]", "", (serial or "").upper())
    if clean_sn.startswith("NEPOZNAT") or len(clean_sn) < 5:
        clean_sn = ""
    clean_sku = re.sub(r"[^A-Z0-9_-]", "", (sku or "").upper().split("#")[0])

    cache_token = clean_sn or clean_sku or re.sub(r"[^A-Z0-9_-]", "_", (model or "").upper()).strip("_")
    cache_path = os.path.join(_get_cache_dir(), f"{vendor or 'GEN'}_{cache_token}.json") if cache_token else ""

    if cache_path and os.path.isfile(cache_path):
        try:
            with open(cache_path, "r", encoding="utf-8") as f:
                cached = json.load(f)
            if cached.get("bom_loaded") and cached.get("schema_version") == 2:
                cached["source"] = f"{cached.get('source', 'bom')} (hub_cache)"
                cached.setdefault("usb_port_count", estimate_usb_port_count(manufacturer, model, is_desktop))
                return cached
        except Exception:
            pass

    spec: Optional[Dict[str, Any]] = None

    if vendor == "HP" and (clean_sn or clean_sku):
        hp_headers = {"Origin": "https://partsurfer.hp.com", "Referer": "https://partsurfer.hp.com/"}
        urls = []
        if clean_sn and clean_sku:
            urls.append(
                f"https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/SerialNumber/GetSerialNumber/{urllib.parse.quote(clean_sn)}/ProductNumber/{urllib.parse.quote(clean_sku)}/country/US/usertype/EXT"
            )
        if clean_sn:
            urls.append(
                f"https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/SerialNumber/GetSerialNumber/{urllib.parse.quote(clean_sn)}/country/US/usertype/EXT"
            )
        elif clean_sku:
            urls.append(
                f"https://partsurfer.hpcloud.hp.com/bff/proxy/get?input=/Search/GenericSearch/{urllib.parse.quote(clean_sku)}/country/US/usertype/EXT"
            )
        for u in urls:
            data = _http_get_json(u, timeout=4.5, headers=hp_headers)
            if isinstance(data, dict):
                body = data.get("Body", {}) or {}
                sbom = body.get("SerialNumberBOM", {}) or {}
                primary: List[Tuple[str, str]] = []
                secondary: List[Tuple[str, str]] = []
                for it in sbom.get("unit_configuration", []) or []:
                    desc = str(it.get("part_description") or "").strip()
                    if desc and desc != "-N/A-":
                        primary.append((str(it.get("part_number") or ""), desc))
                for it in sbom.get("spare_part", []) or []:
                    desc = f"{it.get('spare_part_description') or ''} {it.get('spare_enhance_desc') or ''}".strip()
                    if desc and desc != "-N/A-":
                        secondary.append((str(it.get("spare_part_no") or ""), desc))
                for it in body.get("ProductBOM", []) or []:
                    desc = str(it.get("EnhancedDescription") or "").strip()
                    if desc and desc != "-N/A-":
                        secondary.append((str(it.get("PartNumber") or ""), desc))
                if primary or secondary:
                    spec = _evaluate_bom_entries("hp_partsurfer_bom", primary, secondary, is_desktop)
                    break

    elif vendor == "Lenovo" and (clean_sn or clean_sku):
        urls = []
        if clean_sn:
            urls.append(f"https://pcsupport.lenovo.com/api/v4/upsell/redport/getParts?serialNumber={urllib.parse.quote(clean_sn)}")
        if clean_sku:
            urls.append(f"https://pcsupport.lenovo.com/api/v4/upsell/redport/getProductParts?productId={urllib.parse.quote(clean_sku[:4])}")
        for u in urls:
            data = _http_get_json(u, timeout=4.5)
            if data is not None:
                parts: List[Tuple[str, str]] = []
                _walk_generic_parts(data, parts)
                if len(parts) >= 3:
                    spec = _evaluate_bom_entries("lenovo_parts_api", [], parts, is_desktop)
                    break

    elif vendor == "Dell" and len(clean_sn) == 7:
        for u in (
            f"https://apigtwb2c.us.dell.com/support/assetinfo/v4/getassetparts?servicetags={urllib.parse.quote(clean_sn)}",
            f"https://www.dell.com/support/components/dashboard/api/Asset/GetAssetParts?serviceTag={urllib.parse.quote(clean_sn)}",
        ):
            data = _http_get_json(u, timeout=4.5)
            if data is not None:
                parts = []
                _walk_generic_parts(data, parts)
                if len(parts) >= 3:
                    spec = _evaluate_bom_entries("dell_asset_parts_api", parts, [], is_desktop)
                    break

    if spec is None:
        up_model = (model or "").upper()
        touch_guess: Optional[bool] = None
        touch_ev = ""
        if any(k in up_model for k in ("X360", "2-IN-1", "2 IN 1", "CONVERTIBLE", "DETACHABLE", "TABLET", "SURFACE", "TOUCH", "FOLIO")):
            touch_guess = True
            touch_ev = f"Model ukazuje na Touchscreen ({model})"
        elif is_desktop:
            touch_guess = False
            touch_ev = "Desktop šasija"
        spec = {
            "schema_version": 2,
            "bom_loaded": False,
            "source": "offline_heuristic",
            "summary": "Offline heuristika",
            "has_touchscreen": touch_guess,
            "touchscreen_evidence": touch_ev,
            "has_fingerprint": False if is_desktop else None,
            "fingerprint_evidence": "",
            "has_webcam": False if is_desktop else None,
            "webcam_evidence": "",
            "has_ir_camera": None,
            "ir_camera_evidence": "",
            "has_touchpad": False if is_desktop else True,
            "has_speakers": None,
            "has_microphone": False if is_desktop else None,
            "has_keyboard_iso": None,
            "has_keyboard_numpad": None,
            "keyboard_desc": "",
        }

    spec["usb_port_count"] = estimate_usb_port_count(manufacturer, model, is_desktop)

    if spec.get("bom_loaded") and cache_path:
        try:
            tmp = cache_path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(spec, f, indent=2, ensure_ascii=False)
            os.replace(tmp, cache_path)
        except Exception:
            pass

    return spec
