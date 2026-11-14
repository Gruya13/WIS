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
                return data
        except Exception as e:
            logger.warning(f"Neuspešno čitanje profila za {model_name}: {e}")

    return {
        "model": model_name,
        "normalized_model": norm,
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
    """Čuva profil modela na disk."""
    model_name = profile.get("model", "generic_device")
    path = get_profile_path(model_name)
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(profile, f, indent=2, ensure_ascii=False)
        return True
    except Exception as e:
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
