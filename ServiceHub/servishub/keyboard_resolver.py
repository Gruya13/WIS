"""
servishub.keyboard_resolver
===========================
Centralni koordinator za određivanje kompletnog skupa fizičkih tastera tastature:
1) Potvrđeni profil u storage/model_profiles/<model>.json
2) OEM Internet / Katalog lookup (Lenovo, Dell, HP) sa <= 5s timeout-om
3) Lokalna heuristika (dijagonala ekrana, chassis desktop vs laptop, OS OEM_102)
"""

import logging
from typing import Any, Dict, Optional

from servishub.drivers.oem import get_oem_provider
from servishub.drivers.oem.base import build_keyboard_spec, make_keyboard_layout_name
from servishub.model_profiles import load_model_profile

logger = logging.getLogger("servishub.keyboard_resolver")


def _parse_optional_bool(val: Any) -> Optional[bool]:
    if val is None or val == "":
        return None
    if isinstance(val, bool):
        return val
    s = str(val).strip().lower()
    if s in ("1", "true", "yes", "da"):
        return True
    if s in ("0", "false", "no", "ne"):
        return False
    return None


def _resolve_heuristic_provider(manufacturer: str, model: str, sku: str) -> str:
    """Pokušava da prepozna proizvođača i kada manufacturer nije prosleđen."""
    combined = f"{manufacturer} {model} {sku}".upper()
    if any(k in combined for k in ("LENOVO", "THINKPAD", "IDEAPAD", "THINKCENTRE", "LEGION", "YOGA")):
        return "Lenovo"
    if any(k in combined for k in ("DELL", "LATITUDE", "OPTIPLEX", "PRECISION", "XPS", "VOSTRO", "INSPIRON")):
        return "Dell"
    if any(k in combined for k in ("HP", "HEWLETT", "ELITEBOOK", "PROBOOK", "ZBOOK", "ELITEDESK", "PRODESK")):
        return "HP"
    return manufacturer or ""


def resolve_keyboard_layout(
    manufacturer: str = "",
    model: str = "",
    serial: str = "",
    sku: str = "",
    screen_inches: float = 0.0,
    is_desktop: Optional[bool] = None,
    os_oem102: Optional[bool] = None,
) -> Dict[str, Any]:
    """
    Određuje tačan osnovni šablon tastature i dodatne/uklonjene tastere.
    """
    profile = load_model_profile(model) if model else {"keyboard": {}}
    kb_prof = profile.get("keyboard", {}) if isinstance(profile, dict) else {}
    prof_removed = list(kb_prof.get("removed_keys", []))
    prof_added = list(kb_prof.get("added_keys", []))
    prof_base = kb_prof.get("base_layout")
    prof_confirmed = bool(kb_prof.get("confirmed", False))
    is_thinkpad = "THINKPAD" in f"{manufacturer or ''} {model or ''} {sku or ''}".upper()

    # 1. Ako već postoji ručno potvrđen ili izmenjen profil modela na ServisHub-u
    if prof_base and (prof_confirmed or prof_removed or prof_added):
        prof_is_iso = kb_prof.get("is_iso")
        if prof_is_iso is None:
            prof_is_iso = prof_base.endswith("-iso") or ("Oem102" in prof_added)
        prof_numpad = kb_prof.get("has_numpad")
        if prof_numpad is None:
            prof_numpad = ("numpad" in prof_base) or ("desktop" in prof_base)

        if is_thinkpad and prof_base.startswith("laptop-"):
            if "numpad" in prof_base:
                prof_base = "thinkpad-numpad-iso" if prof_is_iso else "thinkpad-numpad-ansi"
                prof_added = [k for k in prof_added if k not in ("PrintScreen",)]
            else:
                prof_base = "thinkpad-tkl-iso" if prof_is_iso else "thinkpad-tkl-ansi"
                prof_added = [k for k in prof_added if k not in ("Home", "End", "PageUp", "PageDown", "PrintScreen")]

        return build_keyboard_spec(
            status="ok",
            source="model_profile",
            confidence="high",
            base_layout=prof_base,
            has_numpad=bool(prof_numpad),
            is_iso=bool(prof_is_iso),
            has_nav_column=("-nav-" in prof_base),
            has_copilot=("Copilot" in prof_added),
            oem_part_description="",
            added_keys=prof_added,
            removed_keys=prof_removed,
            raw_data={"model": model, "serial": serial, "sku": sku, "is_thinkpad": is_thinkpad},
        )

    # 2. OEM Lookup (Lenovo / Dell / HP)
    oem_res = None
    vendor_hint = _resolve_heuristic_provider(manufacturer, model, sku)
    if vendor_hint:
        try:
            provider = get_oem_provider(vendor_hint)
            if provider is not None:
                oem_res = provider.lookup_keyboard_spec(
                    serial=serial or "",
                    model=model or "",
                    product_number=sku or "",
                )
        except ValueError:
            oem_res = None
        except Exception as exc:
            logger.warning(f"OEM lookup_keyboard_spec greška za {vendor_hint}: {exc}")
            oem_res = None

    has_numpad = None  # type: Optional[bool]
    is_iso = None      # type: Optional[bool]
    has_nav_column = False
    has_copilot = False
    oem_desc = ""
    source = "local_heuristic"
    confidence = "low"
    added = list(prof_added)
    removed = list(prof_removed)

    if oem_res and oem_res.get("status") == "ok":
        has_numpad = oem_res.get("has_numpad")
        is_iso = oem_res.get("is_iso")
        has_nav_column = bool(oem_res.get("has_nav_column", False))
        has_copilot = bool(oem_res.get("has_copilot", False))
        oem_desc = oem_res.get("oem_part_description", "")
        source = oem_res.get("source", "oem_api")
        confidence = oem_res.get("confidence", "medium")
        for k in oem_res.get("added_keys", []):
            if k not in added:
                added.append(k)
        for k in oem_res.get("removed_keys", []):
            if k not in removed:
                removed.append(k)
        oem_raw = oem_res.get("raw_data", {}) or {}
        if oem_raw.get("is_desktop") and is_desktop is None:
            is_desktop = True
        if oem_raw.get("is_thinkpad"):
            is_thinkpad = True

    # 3. Spajanje sa lokalnom hardverskom heuristikom ako neki podatak i dalje nedostaje
    if is_desktop is None:
        is_desktop = False

    if has_numpad is None:
        if is_desktop:
            has_numpad = True
        elif screen_inches and float(screen_inches) >= 15.0:
            has_numpad = True
        elif screen_inches and 0 < float(screen_inches) < 15.0:
            has_numpad = False
        else:
            has_numpad = False

    if is_iso is None:
        if os_oem102 is not None:
            is_iso = bool(os_oem102)
            if source == "local_heuristic":
                source = "os_layout_heuristic"
        else:
            is_iso = False

    if not is_desktop and not has_numpad and not is_thinkpad:
        nav_set = {"Home", "PageUp", "PageDown", "End"}
        if has_nav_column or (nav_set.issubset(set(added)) and not any(k in removed for k in nav_set)):
            has_nav_column = True
            added = [k for k in added if k not in nav_set]
    else:
        has_nav_column = False

    if is_thinkpad and not is_desktop:
        strip_set = {"PrintScreen"} if has_numpad else {"Home", "End", "PageUp", "PageDown", "PrintScreen"}
        added = [k for k in added if k not in strip_set]

    base_layout = make_keyboard_layout_name(
        bool(has_numpad),
        bool(is_iso),
        bool(is_desktop),
        has_nav_column=has_nav_column,
        is_thinkpad=(is_thinkpad and not is_desktop),
    )

    # Ako OEM API nije znao region, ali je is_iso iz OS-a potvrđen -> osiguraj Oem102 u added ako treba
    res = build_keyboard_spec(
        status="ok",
        source=source,
        confidence=confidence,
        base_layout=base_layout,
        has_numpad=bool(has_numpad),
        is_iso=bool(is_iso),
        has_nav_column=has_nav_column,
        has_copilot=has_copilot,
        oem_part_description=oem_desc,
        added_keys=added,
        removed_keys=removed,
        raw_data={
            "manufacturer": manufacturer,
            "model": model,
            "serial": serial,
            "sku": sku,
            "screen_inches": screen_inches,
            "is_desktop": bool(is_desktop),
            "is_thinkpad": bool(is_thinkpad),
            "os_oem102": os_oem102,
        },
    )
    return res
