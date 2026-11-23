"""
servishub.model_profiles
========================
Upravljanje profilima modela uređaja (specifični rasporedi tastature i broj USB portova).
Podaci se perzistiraju u storage/model_profiles/<normalized_model>.json.
"""

import os
import re
import json
import logging
from datetime import datetime
from typing import Optional
from servishub.config import active_config

logger = logging.getLogger("servishub.model_profiles")

_MAX_USB_PORTS = 64
_MAX_CHARGER_PORTS = 64
_DEFAULT_MAX_TYPEC_CAP = 2
_MAX_TYPEC_CAP = 16
_MAX_LOCATION_LEN = 256
_MAX_LOCATIONS_LIMIT = 64
_MAX_OBSERVED_COUNTS = 20
_STRONG_USB_LOCATION_RE = re.compile(
    r"^(?:PCIROOT|ACPI)\([A-Za-z0-9_.:-]+\)(?:#[A-Za-z0-9_.:-]+\([A-Za-z0-9_.:-]+\))+$",
    re.IGNORECASE,
)


def _is_strong_usb_location(loc: str) -> bool:
    """Proverava da li je lokacija jaka PCIROOT / ACPI putanja (nikada AUTO-USBC-CHARGER)."""
    if not isinstance(loc, str):
        return False
    clean = loc.strip()
    if not clean or len(clean) > _MAX_LOCATION_LEN:
        return False
    if "AUTO-USBC-CHARGER" in clean.upper():
        return False
    return bool(_STRONG_USB_LOCATION_RE.match(clean))


def _sanitize_physical_locations(raw_locations) -> list:
    """Validira, filtrira samo jake PCIROOT/ACPI lokacije i deduplikuje (case-insensitive)."""
    if raw_locations is None:
        return []
    if not isinstance(raw_locations, (list, tuple, set)):
        raise ValueError("Polje 'physical_port_locations' mora biti lista stringova")
    seen_upper = set()
    cleaned = []
    for item in list(raw_locations)[:_MAX_LOCATIONS_LIMIT]:
        if not isinstance(item, str):
            continue
        loc = item.strip()
        if not _is_strong_usb_location(loc):
            continue
        key = loc.upper()
        if key not in seen_upper:
            seen_upper.add(key)
            cleaned.append(loc)
    return cleaned


def _parse_bounded_int(val, field_name: str, min_val: int, max_val: int) -> Optional[int]:
    if val is None:
        return None
    if isinstance(val, bool):
        raise ValueError(f"Polje '{field_name}' ne može biti boolean")
    if isinstance(val, float) and not val.is_integer():
        raise ValueError(f"Polje '{field_name}' mora biti ceo broj")
    try:
        ival = int(val)
    except (ValueError, TypeError) as exc:
        raise ValueError(f"Polje '{field_name}' mora biti ceo broj") from exc
    if ival < min_val or ival > max_val:
        raise ValueError(f"Polje '{field_name}' mora biti između {min_val} i {max_val}")
    return ival


def _normalize_usb_section(raw_usb) -> dict:
    """Normalizuje USB sekciju profila uz unazadnu kompatibilnost za stare profile."""
    usb = dict(raw_usb) if isinstance(raw_usb, dict) else {}
    port_count = usb.get("port_count")
    if isinstance(port_count, bool):
        port_count = None
    elif port_count is not None:
        try:
            port_count = int(port_count)
        except (ValueError, TypeError):
            port_count = None
    if port_count is not None and (port_count < 1 or port_count > _MAX_USB_PORTS):
        port_count = None

    usb["port_count"] = port_count
    if isinstance(usb.get("confirmed"), bool):
        confirmed = usb["confirmed"]
    else:
        confirmed = port_count is not None
    usb["confirmed"] = confirmed

    source = usb.get("source")
    if source is not None:
        source = str(source).strip()[:64] or None
    if source is None and port_count is not None:
        source = "manual_override"
    usb["source"] = source

    try:
        usb["known_port_locations"] = _sanitize_physical_locations(usb.get("known_port_locations") or [])
    except ValueError:
        usb["known_port_locations"] = []

    raw_obs = usb.get("observed_counts")
    if isinstance(raw_obs, list):
        usb["observed_counts"] = [x for x in raw_obs if isinstance(x, dict)][-_MAX_OBSERVED_COUNTS:]
    else:
        usb["observed_counts"] = []
    return usb

def normalize_model_name(raw_model: str) -> str:
    """Normalizuje naziv modela u bezbedan identifikator za fajl sistem."""
    if not raw_model or not isinstance(raw_model, str):
        return "generic_device"
    clean = raw_model.strip().lower()
    clean = re.sub(r"[^\w\s-]", "_", clean)
    clean = re.sub(r"[\s_]+", "_", clean)
    clean = clean.strip("_")
    return clean or "generic_device"

def get_profiles_dir() -> str:
    """Vraća putanju do foldera sa profilima modela."""
    base = active_config.base_dir
    pdir = os.path.join(base, "storage", "model_profiles")
    os.makedirs(pdir, exist_ok=True)
    return pdir

def get_profile_path(model_name: str) -> str:
    norm = normalize_model_name(model_name)
    return os.path.join(get_profiles_dir(), f"{norm}.json")

def load_model_profile(model_name: str) -> dict:
    """Učitava profil modela ili vraća podrazumevani ako ne postoji."""
    norm = normalize_model_name(model_name)
    path = get_profile_path(model_name)
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
                data["normalized_model"] = norm
                data.setdefault("manufacturer", "")
                data.setdefault("preferred_os_build", "")
                data.setdefault("known_hwids", [])
                data["usb"] = _normalize_usb_section(data.get("usb"))
                return data
        except Exception as e:
            logger.warning(f"Neuspešno čitanje profila za {model_name}: {e}")

    return {
        "model": model_name,
        "normalized_model": norm,
        "manufacturer": "",
        "preferred_os_build": "",
        "known_hwids": [],
        "keyboard": {
            "base_layout": None,
            "is_iso": None,
            "has_numpad": None,
            "confirmed": False,
            "source": None,
            "removed_keys": [],
            "added_keys": []
        },
        "usb": {
            "port_count": None,
            "confirmed": False,
            "source": None,
            "known_port_locations": [],
            "observed_counts": [],
        },
        "history": []
    }

def save_model_profile(profile: dict) -> bool:
    """Atomično čuva profil modela na disk preko privremenog fajla."""
    model_name = profile.get("model", "generic_device")
    path = get_profile_path(model_name)
    tmp_path = f"{path}.tmp"
    try:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(profile, f, indent=2, ensure_ascii=False)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp_path, path)
        return True
    except Exception as e:
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except OSError:
                pass
        logger.error(f"Greška pri čuvanju profila za {model_name}: {e}")
        return False

def update_keyboard_override(model_name: str, removed_keys: list = None, added_keys: list = None,
                             by: str = "serviser", base_layout: str = None,
                             is_iso: Optional[bool] = None, has_numpad: Optional[bool] = None,
                             confirmed: bool = True, source: str = None) -> dict:
    """Ažurira specifikaciju tastera za model sa revizijom istorije."""
    profile = load_model_profile(model_name)
    kb = profile.setdefault("keyboard", {})
    if base_layout:
        kb["base_layout"] = base_layout
        # Ako je osnovni šablon promenjen na ISO ili Numpad, automatski izvedi flagove ako nisu prosleđeni
        if is_iso is None:
            if base_layout.endswith("-iso"):
                is_iso = True
            elif base_layout.endswith("-ansi"):
                is_iso = False
        if has_numpad is None:
            if "numpad" in base_layout or "desktop" in base_layout:
                has_numpad = True
            elif "tkl" in base_layout:
                has_numpad = False

    if is_iso is not None:
        kb["is_iso"] = bool(is_iso)
    if has_numpad is not None:
        kb["has_numpad"] = bool(has_numpad)
    kb["confirmed"] = bool(confirmed)
    if source:
        kb["source"] = source

    current_removed = set(kb.get("removed_keys", []))
    current_added = set(kb.get("added_keys", []))

    if removed_keys:
        for k in removed_keys:
            if k:
                current_removed.add(k)
                current_added.discard(k)

    if added_keys:
        for k in added_keys:
            if k:
                current_added.add(k)
                current_removed.discard(k)

    kb["removed_keys"] = sorted(list(current_removed))
    kb["added_keys"] = sorted(list(current_added))

    now_iso = datetime.now().isoformat()
    profile.setdefault("history", []).append({
        "type": "keyboard_override",
        "timestamp": now_iso,
        "by": by,
        "details": {
            "base_layout": base_layout,
            "is_iso": is_iso,
            "has_numpad": has_numpad,
            "removed_keys": removed_keys or [],
            "added_keys": added_keys or []
        }
    })
    save_model_profile(profile)
    return profile

def update_usb_override(
    model_name: str,
    port_count: Optional[int] = None,
    by: str = "serviser",
    target_ports: Optional[int] = None,
    detected_ports: Optional[int] = None,
    physical_port_locations: Optional[list] = None,
    charger_ports_count: Optional[int] = None,
    max_typec_cap: Optional[int] = None,
    manual_override: Optional[bool] = None,
    serial: str = "",
) -> dict:
    """
    Ažurira USB profil modela uz unazadnu kompatibilnost (manual override)
    i samoučenje fizičkih USB portova sa anti-poisoning zaštitom.
    """
    if not isinstance(model_name, str) or not model_name.strip():
        raise ValueError("Nedostaje validno ime modela")

    port_count_int = _parse_bounded_int(port_count, "port_count", 1, _MAX_USB_PORTS)
    target_int = _parse_bounded_int(target_ports, "target_ports", 0, _MAX_USB_PORTS)
    detected_int = _parse_bounded_int(detected_ports, "detected_ports", 0, _MAX_USB_PORTS)
    charger_int = _parse_bounded_int(charger_ports_count, "charger_ports_count", 0, _MAX_CHARGER_PORTS)
    cap_int = _parse_bounded_int(max_typec_cap, "max_typec_cap", 0, _MAX_TYPEC_CAP)
    effective_cap = _DEFAULT_MAX_TYPEC_CAP if cap_int is None else max(0, cap_int)

    if manual_override is not None and not isinstance(manual_override, bool):
        raise ValueError("Polje 'manual_override' mora biti boolean")
    if by is not None and not isinstance(by, str):
        raise ValueError("Polje 'by' mora biti string")
    if serial is not None and not isinstance(serial, str):
        raise ValueError("Polje 'serial' mora biti string")

    clean_locs = _sanitize_physical_locations(physical_port_locations)

    has_autolearn_fields = any(
        v is not None
        for v in (target_ports, detected_ports, physical_port_locations, charger_ports_count, max_typec_cap)
    )
    is_manual = (not has_autolearn_fields) if manual_override is None else bool(manual_override)

    if is_manual and port_count_int is None:
        raise ValueError("Nedostaje polje 'port_count' za manual override")

    clean_by = str(by or "serviser").strip()[:64] or "serviser"
    clean_serial = re.sub(r"[^A-Za-z0-9_-]", "", str(serial or "").strip().upper())[:64]

    profile = load_model_profile(model_name)
    usb = _normalize_usb_section(profile.get("usb"))
    profile["usb"] = usb

    now_iso = datetime.now().isoformat()

    # Unija jakih PCIROOT / ACPI fizičkih lokacija (uvek deduplikovano i ograničeno)
    known_locs = list(usb.get("known_port_locations") or [])
    known_upper = {loc.upper() for loc in known_locs}
    for loc in clean_locs:
        if len(known_locs) >= _MAX_LOCATIONS_LIMIT:
            break
        key = loc.upper()
        if key not in known_upper:
            known_upper.add(key)
            known_locs.append(loc)
    usb["known_port_locations"] = known_locs

    if is_manual:
        # Pravilo 3: Ručni override postavlja port_count, source="manual_override", confirmed=True
        prev_port_count = usb.get("port_count")
        usb["port_count"] = int(port_count_int)
        usb["confirmed"] = True
        usb["source"] = "manual_override"
        usb["observed_counts"] = []

        profile.setdefault("history", []).append({
            "type": "usb_override",
            "timestamp": now_iso,
            "by": clean_by,
            "details": {
                "port_count": int(port_count_int),
                "previous_port_count": prev_port_count,
                "manual_override": True,
                "serial": clean_serial,
            },
        })
        save_model_profile(profile)
        return profile

    # Auto-learn mod (Pravilo 1 i Pravilo 2)
    prev_port_count = usb.get("port_count")
    was_confirmed = bool(usb.get("confirmed", False)) and prev_port_count is not None
    current_count = int(prev_port_count) if prev_port_count is not None else 0

    physical_count = len(clean_locs)
    charger_count = charger_int if charger_int is not None else 0
    capped_charger = min(charger_count, effective_cap)
    effective_ports = physical_count + capped_charger

    observed = list(usb.get("observed_counts") or [])
    if physical_count > 0 or charger_count > 0 or (detected_int is not None and detected_int > 0):
        obs_entry = {
            "detected": int(detected_int if detected_int is not None else effective_ports),
            "physical": physical_count,
            "charger": charger_count,
            "effective": effective_ports,
            "serial": clean_serial,
            "ts": now_iso,
        }
        observed.append(obs_entry)
        observed = observed[-_MAX_OBSERVED_COUNTS:]
        usb["observed_counts"] = observed

    known_count = len(known_locs)
    new_count = current_count
    autolearn_reason = None

    # Pravilo 1: Nepobitni fizički dokaz iz unije known_port_locations
    if known_count > new_count:
        new_count = known_count
        autolearn_reason = "known_port_locations"

    # Pravilo 2: Kombinacija fizičkih portova + ograničen USB-C punjač
    if effective_ports > 0:
        if not was_confirmed:
            if effective_ports > new_count:
                new_count = effective_ports
                autolearn_reason = autolearn_reason or "initial_effective_ports"
            elif new_count > 0 and not autolearn_reason:
                autolearn_reason = "initial_confirm"
        elif effective_ports > new_count:
            confirming_serials = set()
            for item in observed:
                item_sn = str(item.get("serial") or "").strip().upper()
                if not item_sn:
                    continue
                item_eff = item.get("effective")
                if item_eff is None:
                    try:
                        item_eff = int(item.get("physical", 0)) + min(int(item.get("charger", 0)), _DEFAULT_MAX_TYPEC_CAP)
                    except (ValueError, TypeError):
                        item_eff = 0
                if int(item_eff) >= effective_ports:
                    confirming_serials.add(item_sn)
            if len(confirming_serials) >= 2:
                new_count = effective_ports
                autolearn_reason = "multi_serial_effective_ports"

    if new_count > 0 and (known_count > 0 or effective_ports > 0) and (new_count != current_count or not was_confirmed):
        usb["port_count"] = int(new_count)
        usb["confirmed"] = True
        usb["source"] = "autolearn"
        profile.setdefault("history", []).append({
            "type": "usb_autolearn",
            "timestamp": now_iso,
            "by": "serviser_autolearn",
            "details": {
                "port_count": int(new_count),
                "previous_port_count": prev_port_count,
                "known_locations_count": known_count,
                "effective_ports": effective_ports,
                "physical_count": physical_count,
                "charger_ports_count": charger_count,
                "max_typec_cap": effective_cap,
                "target_ports": target_int,
                "detected_ports": detected_int,
                "serial": clean_serial,
                "rule": autolearn_reason or "autolearn",
            },
        })

    save_model_profile(profile)
    return profile


def _clean_hwid_token(raw_hwid: str) -> str:
    if not raw_hwid or not isinstance(raw_hwid, str):
        return ""
    cleaned = re.sub(r"\\+", r"\\", raw_hwid.strip())
    if not cleaned or "\\" not in cleaned:
        return ""
    if not re.match(r"^[A-Za-z0-9_.-]+\\.+", cleaned):
        return ""
    return cleaned


def update_model_dna(
    model_name: str,
    hwids: list,
    manufacturer: str = "",
    os_build: str = "",
    by: str = "wis_client",
    model_aliases: list = None,
) -> dict:
    """Ažurira poznati HWID otisak (Model DNA) za zadati model računara i povezane aliase (MTM ⬌ naziv modela)."""
    clean_aliases = []
    seen_alias_norms = set()
    raw_candidates = [model_name] + (list(model_aliases) if isinstance(model_aliases, (list, tuple, set)) else [])
    for cand in raw_candidates:
        if isinstance(cand, str) and cand.strip():
            c_val = cand.strip()
            c_norm = normalize_model_name(c_val)
            if c_norm and c_norm not in seen_alias_norms:
                seen_alias_norms.add(c_norm)
                clean_aliases.append(c_val)

    profile = load_model_profile(model_name)
    alias_profiles = {}
    for al_name in clean_aliases:
        al_norm = normalize_model_name(al_name)
        if al_norm != normalize_model_name(model_name) and os.path.isfile(get_profile_path(al_name)):
            alias_profiles[al_name] = load_model_profile(al_name)

    mfg_val = manufacturer.strip() if isinstance(manufacturer, str) and manufacturer.strip() else ""
    if not mfg_val:
        mfg_val = str(profile.get("manufacturer") or "").strip()
        if not mfg_val:
            for ap in alias_profiles.values():
                if ap.get("manufacturer"):
                    mfg_val = str(ap["manufacturer"]).strip()
                    break

    build_val = os_build.strip() if isinstance(os_build, str) and os_build.strip() else ""
    if not build_val:
        build_val = str(profile.get("preferred_os_build") or "").strip()
        if not build_val:
            for ap in alias_profiles.values():
                if ap.get("preferred_os_build"):
                    build_val = str(ap["preferred_os_build"]).strip()
                    break

    if mfg_val:
        profile["manufacturer"] = mfg_val
    if build_val:
        profile["preferred_os_build"] = build_val

    by_lower = {}
    existing_sources = [profile.get("known_hwids") or []]
    for ap in alias_profiles.values():
        existing_sources.append(ap.get("known_hwids") or [])
    for h_list in existing_sources:
        for h in h_list:
            c = _clean_hwid_token(h)
            if c:
                by_lower.setdefault(c.lower(), c)

    if isinstance(hwids, str):
        raw_iter = [hwids]
    elif isinstance(hwids, (list, tuple, set)):
        raw_iter = list(hwids)
    else:
        raw_iter = []

    added_count = 0
    for item in raw_iter:
        c = _clean_hwid_token(item)
        if c:
            key = c.lower()
            if key not in by_lower:
                by_lower[key] = c
                added_count += 1

    merged_sorted = [by_lower[k] for k in sorted(by_lower.keys())]
    prev_primary_len = len(profile.get("known_hwids") or [])
    profile["known_hwids"] = merged_sorted

    merged_aliases = []
    seen_al_keys = set()
    for al in (list(profile.get("aliases") or []) + clean_aliases):
        if isinstance(al, str) and al.strip():
            ak = normalize_model_name(al)
            if ak not in seen_al_keys:
                seen_al_keys.add(ak)
                merged_aliases.append(al.strip())
    if merged_aliases:
        profile["aliases"] = merged_aliases

    now_iso = datetime.now().isoformat()
    if added_count > 0 or len(merged_sorted) != prev_primary_len or not profile.get("dna_updated_at"):
        profile["dna_updated_at"] = now_iso

    if added_count > 0:
        profile.setdefault("history", []).append({
            "type": "model_dna_sync",
            "timestamp": now_iso,
            "by": by,
            "details": {
                "added_count": added_count,
                "total_hwids": len(merged_sorted),
                "os_build": profile.get("preferred_os_build", ""),
                "aliases": merged_aliases,
            },
        })

    if added_count > 0 or len(merged_sorted) != prev_primary_len or mfg_val or build_val or len(merged_aliases) > 1 or not os.path.isfile(get_profile_path(model_name)):
        save_model_profile(profile)

    # Sinhronizacija povezanih alias profila (npr. MTM '20W0009HMH' <-> 'Lenovo ThinkPad T14 Gen 2i')
    primary_norm = normalize_model_name(model_name)
    for al_name in merged_aliases:
        al_norm = normalize_model_name(al_name)
        if not al_norm or al_norm == primary_norm:
            continue
        ap = alias_profiles.get(al_name) or load_model_profile(al_name)
        ap_changed = False
        if mfg_val and ap.get("manufacturer") != mfg_val:
            ap["manufacturer"] = mfg_val
            ap_changed = True
        if build_val and ap.get("preferred_os_build") != build_val:
            ap["preferred_os_build"] = build_val
            ap_changed = True
        if ap.get("known_hwids") != merged_sorted:
            ap["known_hwids"] = list(merged_sorted)
            ap["dna_updated_at"] = profile.get("dna_updated_at", now_iso)
            ap_changed = True
        if ap.get("aliases") != merged_aliases:
            ap["aliases"] = list(merged_aliases)
            ap_changed = True
        if ap_changed or not os.path.isfile(get_profile_path(al_name)):
            save_model_profile(ap)

    return profile


def list_model_profiles() -> list:
    """Vraća sažetak svih sačuvanih profila modela i njihovih Model DNA zapisa."""
    pdir = get_profiles_dir()
    items = []
    if not os.path.isdir(pdir):
        return items

    for fname in sorted(os.listdir(pdir)):
        if not fname.endswith(".json"):
            continue
        fpath = os.path.join(pdir, fname)
        if not os.path.isfile(fpath):
            continue
        try:
            with open(fpath, "r", encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                continue
            known_hwids = [h for h in (data.get("known_hwids") or []) if isinstance(h, str) and h.strip()]
            kb = data.get("keyboard") if isinstance(data.get("keyboard"), dict) else {}
            items.append({
                "model": data.get("model") or fname[:-5],
                "normalized_model": data.get("normalized_model") or fname[:-5],
                "manufacturer": data.get("manufacturer", ""),
                "aliases": data.get("aliases") or [],
                "known_hwids_count": len(known_hwids),
                "known_hwids": known_hwids,
                "preferred_os_build": data.get("preferred_os_build", ""),
                "keyboard_confirmed": bool(kb.get("confirmed", False)),
                "dna_updated_at": data.get("dna_updated_at", ""),
            })
        except Exception:
            continue

    items.sort(key=lambda x: (-x["known_hwids_count"], str(x["model"]).lower()))
    return items
