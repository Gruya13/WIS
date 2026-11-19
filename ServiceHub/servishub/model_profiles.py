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
            "port_count": None
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

def update_usb_override(model_name: str, port_count: int, by: str = "serviser") -> dict:
    """Ažurira broj USB portova za model sa revizijom istorije."""
    profile = load_model_profile(model_name)
    profile.setdefault("usb", {})["port_count"] = int(port_count)

    now_iso = datetime.now().isoformat()
    profile.setdefault("history", []).append({
        "type": "usb_override",
        "timestamp": now_iso,
        "by": by,
        "details": {
            "port_count": int(port_count)
        }
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
