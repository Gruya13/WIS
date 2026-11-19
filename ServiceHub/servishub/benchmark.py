"""
servishub.benchmark
===================
Agregacija i analiza WIS Benchmark metrika na nivou pojedinačnih instalacija i cele flote:
- Trajanje po fazama (Phase1..Phase6), prosečno vreme, P50, P90 i usko grlo (bottleneck_phase)
- Mrežni throughput (LAN vs WAN u Mbps)
- Cache Hit Ratio (po broju zahteva i po količini prenetih bajtova)
- First-Pass Success Rate (%)
"""

import json
import math
import os
import re
from datetime import datetime
from typing import Any, Dict, List, Optional

from servishub.drivers.cache import sanitize_id
from servishub.model_profiles import list_model_profiles, normalize_model_name
from servishub.reports import compute_handover_status

PHASE_KEYS = ("Phase1", "Phase2", "Phase3", "Phase4", "Phase5", "Phase6")

_GENERIC_MODEL_LABELS = {
    "",
    "nepoznat model",
    "nepoznato",
    "unknown",
    "unknown model",
    "unknown_model",
    "genericpc",
    "to be filled by o.e.m.",
    "system product name",
    "default string",
}

_BUILTIN_MTM_MODEL_MAP = {
    "20w0009hmh": "LENOVO ThinkPad T14 Gen 2i",
    "20w00047mh": "LENOVO ThinkPad T14 Gen 2i",
    "20w0004dmh": "LENOVO ThinkPad T14 Gen 2i",
    "21bys0211g": "LENOVO ThinkPad X13s Gen 1",
    "11t300bgmh": "LENOVO ThinkCentre M70q Gen 3",
    "11t3002vmh": "LENOVO ThinkCentre M70q Gen 3",
    "21mms60u0t": "LENOVO ThinkPad T14 Gen 5",
}


def _parse_duration_str(val: Any) -> float:
    """Pretvara string trajanja ('00:11:19', '6m 50s', '123s') u sekunde."""
    if val is None or isinstance(val, bool):
        return 0.0
    if isinstance(val, (int, float)):
        return max(0.0, float(val))
    raw = str(val).strip()
    if not raw:
        return 0.0
    if re.fullmatch(r"\d+(?:\.\d+)?", raw):
        return max(0.0, float(raw))
    m_hms = re.fullmatch(r"(\d{1,3}):(\d{1,2}):(\d{1,2})", raw)
    if m_hms:
        return float(int(m_hms.group(1)) * 3600 + int(m_hms.group(2)) * 60 + int(m_hms.group(3)))
    m_ms = re.fullmatch(r"(\d{1,3}):(\d{1,2})", raw)
    if m_ms:
        return float(int(m_ms.group(1)) * 60 + int(m_ms.group(2)))
    total = 0.0
    matched = False
    for unit_re, mult in ((r"(\d+)\s*h", 3600.0), (r"(\d+)\s*m", 60.0), (r"(\d+)\s*s", 1.0)):
        mu = re.search(unit_re, raw, re.IGNORECASE)
        if mu:
            total += int(mu.group(1)) * mult
            matched = True
    return total if matched else 0.0


def _safe_float(val: Any, default: float = 0.0) -> float:
    if val is None or isinstance(val, bool):
        return default
    try:
        return float(val)
    except (ValueError, TypeError):
        return default


def _safe_int(val: Any, default: int = 0) -> int:
    if val is None or isinstance(val, bool):
        return default
    try:
        return int(float(val))
    except (ValueError, TypeError):
        return default


def _parse_ts_score(data: dict, fallback_mtime: float = 0.0) -> float:
    raw = data.get("timestamp") or data.get("last_seen")
    if isinstance(raw, (int, float)) and not isinstance(raw, bool):
        return float(raw)
    if isinstance(raw, str) and raw.strip():
        try:
            return datetime.fromisoformat(raw.strip().replace("Z", "+00:00")).timestamp()
        except Exception:
            for fmt in ("%Y-%m-%d %H:%M:%S", "%Y%m%d_%H%M%S", "%Y-%m-%dT%H:%M:%S"):
                try:
                    return datetime.strptime(raw.strip(), fmt).timestamp()
                except ValueError:
                    pass
    return fallback_mtime


def _percentile(sorted_vals: List[float], pct: float) -> float:
    if not sorted_vals:
        return 0.0
    if len(sorted_vals) == 1:
        return round(sorted_vals[0], 1)
    idx = (pct / 100.0) * (len(sorted_vals) - 1)
    lower = int(math.floor(idx))
    upper = int(math.ceil(idx))
    if lower == upper:
        return round(sorted_vals[lower], 1)
    weight = idx - lower
    val = sorted_vals[lower] * (1.0 - weight) + sorted_vals[upper] * weight
    return round(val, 1)


def extract_device_benchmark(report_data: dict) -> dict:
    """
    Izvlači i normalizuje benchmark metrike iz jednog izveštaja uređaja.
    Ako izveštaj nema eksplicitni `benchmark` blok, rekonstruiše baseline iz `duration_seconds`.
    """
    if not isinstance(report_data, dict):
        report_data = {}

    nested = report_data.get("latest_report") if isinstance(report_data.get("latest_report"), dict) else {}
    bm = report_data.get("benchmark")
    if not isinstance(bm, dict):
        bm = nested.get("benchmark") if isinstance(nested.get("benchmark"), dict) else {}

    raw_phases = bm.get("phase_durations_sec") if isinstance(bm.get("phase_durations_sec"), dict) else {}
    phase_durations = {}
    phase_sum = 0.0
    for pk in PHASE_KEYS:
        val = max(0.0, _safe_float(raw_phases.get(pk), 0.0))
        phase_durations[pk] = round(val, 2) if not val.is_integer() else int(val)
        phase_sum += val

    dur_sec = _safe_float(bm.get("total_phase_duration_sec"), 0.0)
    if dur_sec <= 0.0:
        dur_sec = _safe_float(report_data.get("duration_seconds"), 0.0)
    if dur_sec <= 0.0 and nested:
        dur_sec = _safe_float(nested.get("duration_seconds"), 0.0)
    if dur_sec <= 0.0 and phase_sum > 0.0:
        dur_sec = phase_sum
    if dur_sec <= 0.0:
        dur_sec = _parse_duration_str(
            report_data.get("duration")
            or nested.get("duration")
            or report_data.get("duration_formatted")
            or nested.get("duration_formatted")
        )

    hits = max(0, _safe_int(bm.get("cache_hits"), 0))
    misses = max(0, _safe_int(bm.get("cache_misses"), 0))
    drv_hits = max(0, _safe_int(bm.get("driver_hits"), 0))
    drv_misses = max(0, _safe_int(bm.get("driver_misses"), 0))
    upd_hits = max(0, _safe_int(bm.get("update_hits"), 0))
    upd_misses = max(0, _safe_int(bm.get("update_misses"), 0))

    lan_bytes = max(0, _safe_int(bm.get("lan_bytes"), 0))
    wan_bytes = max(0, _safe_int(bm.get("wan_bytes"), 0))
    lan_mbps = max(0.0, _safe_float(bm.get("lan_throughput_mbps"), 0.0))
    wan_mbps = max(0.0, _safe_float(bm.get("wan_throughput_mbps"), 0.0))

    total_reqs = hits + misses
    if bm.get("cache_hit_ratio_pct") is not None and total_reqs > 0:
        hit_ratio = round(_safe_float(bm.get("cache_hit_ratio_pct"), 0.0), 1)
    elif total_reqs > 0:
        hit_ratio = round((hits / float(total_reqs)) * 100.0, 1)
    else:
        hit_ratio = None

    total_bytes = lan_bytes + wan_bytes
    if bm.get("byte_hit_ratio_pct") is not None and total_bytes > 0:
        byte_ratio = round(_safe_float(bm.get("byte_hit_ratio_pct"), 0.0), 1)
    elif total_bytes > 0:
        byte_ratio = round((lan_bytes / float(total_bytes)) * 100.0, 1)
    else:
        byte_ratio = None

    hwtest_obj = report_data.get("hwtest") if isinstance(report_data.get("hwtest"), dict) else (
        nested.get("hwtest") if isinstance(nested.get("hwtest"), dict) else {}
    )
    hwtest_summary = hwtest_obj.get("summary") if isinstance(hwtest_obj.get("summary"), dict) else {}
    raw_op_wait = hwtest_summary.get("operator_wait_sec")
    raw_active_hw = hwtest_summary.get("active_hwtest_sec")
    op_wait_sec = round(_safe_float(raw_op_wait, 0.0), 1) if raw_op_wait is not None else None
    active_hw_sec = round(_safe_float(raw_active_hw, 0.0), 1) if raw_active_hw is not None else None

    return {
        "has_benchmark": bool(bm),
        "phase_durations_sec": phase_durations,
        "total_duration_sec": round(dur_sec, 1) if dur_sec > 0 else 0,
        "cache_hits": hits,
        "cache_misses": misses,
        "driver_hits": drv_hits,
        "driver_misses": drv_misses,
        "update_hits": upd_hits,
        "update_misses": upd_misses,
        "cache_hit_ratio_pct": hit_ratio,
        "byte_hit_ratio_pct": byte_ratio,
        "lan_bytes": lan_bytes,
        "wan_bytes": wan_bytes,
        "lan_throughput_mbps": round(lan_mbps, 2),
        "wan_throughput_mbps": round(wan_mbps, 2),
        "operator_wait_sec": op_wait_sec,
        "active_hwtest_sec": active_hw_sec,
    }


def _is_finished_record(rec: dict) -> bool:
    if not isinstance(rec, dict):
        return False
    if isinstance(rec.get("benchmark"), dict) or isinstance(rec.get("latest_report"), dict):
        return True
    st = str(rec.get("deployment_status") or rec.get("status") or "").upper()
    if st in ("COMPLETED", "SUCCESS", "OK", "DONE", "NEEDS_ATTENTION", "ERROR", "FAILED", "WARN", "WARNING"):
        return True
    lbl = str(rec.get("status_label") or "")
    if lbl in ("Zavrseno", "Završeno", "NEEDS_ATTENTION", "Servisiran (AGY)", "Kvar (AGY)"):
        return True
    if _safe_float(rec.get("duration_seconds"), 0.0) > 0:
        return True
    if _parse_duration_str(rec.get("duration")) > 0:
        return True
    return False


def _clean_display_model_label(raw_mfg: str, raw_model: str) -> str:
    m = str(raw_model or "").strip()
    mfg = str(raw_mfg or "").strip()
    if m.lower() in _GENERIC_MODEL_LABELS:
        return "Nepoznat Model"
    # Ukloni duplirane prefikse tipa 'HP HP EliteBook' ili 'LENOVO LENOVO ThinkPad'
    m = re.sub(r"^(HP|LENOVO|DELL|ASUS|ACER|FUJITSU)\s+\1\b\s*", r"\1 ", m, flags=re.IGNORECASE).strip()
    if not mfg or mfg.lower() in ("nepoznato", "unknown", "to be filled by o.e.m.", "system manufacturer"):
        return m
    if mfg.lower() in m.lower():
        return m
    return f"{mfg} {m}".strip()


def _build_model_alias_map() -> Dict[str, str]:
    alias_map: Dict[str, str] = dict(_BUILTIN_MTM_MODEL_MAP)
    try:
        profiles = list_model_profiles()
    except Exception:
        profiles = []

    for prof in profiles:
        if not isinstance(prof, dict):
            continue
        mfg = str(prof.get("manufacturer") or "").strip()
        raw_names = []
        if prof.get("model"):
            raw_names.append(str(prof["model"]).strip())
        for al in (prof.get("aliases") or []):
            if isinstance(al, str) and al.strip():
                raw_names.append(al.strip())
        if not raw_names:
            continue

        # Izaberi najopisniji marketinški naziv (sa razmakom / serijom umesto čistog MTM koda)
        def _rank_name(n: str):
            has_space = 1 if " " in n else 0
            has_brand_hint = 1 if any(t in n.lower() for t in ("thinkpad", "thinkcentre", "elitebook", "probook", "latitude", "optiplex", "lifebook", "vostro", "ideapad", "prodesk", "elitedesk", "elite")) else 0
            return (has_brand_hint, has_space, len(n))

        best_raw = max(raw_names, key=_rank_name)
        canonical = _clean_display_model_label(mfg, best_raw)
        if canonical == "Nepoznat Model":
            continue
        for rn in raw_names:
            norm = normalize_model_name(rn)
            if norm and norm not in ("generic_device", "nepoznat_model"):
                # Ne prepisuj marketinško ime ako je canonical samo jednorečni MTM bez boljeg naziva
                if " " in canonical or norm not in alias_map:
                    alias_map[norm] = canonical
    return alias_map


def _resolve_canonical_model_label(raw_mfg: str, raw_model: str, alias_map: Optional[Dict[str, str]] = None) -> str:
    cleaned = _clean_display_model_label(raw_mfg, raw_model)
    if cleaned == "Nepoznat Model":
        return cleaned
    if alias_map:
        for candidate in (str(raw_model or "").strip(), cleaned):
            norm = normalize_model_name(candidate)
            if norm and norm in alias_map:
                return alias_map[norm]
    return cleaned


def _merge_report_records(
    prev_entry: Optional[tuple],
    incoming_ts: float,
    incoming_rec: dict,
) -> tuple:
    """Spaja parcijalne i pune izveštaje istog uređaja bez gubitka `hw`, `benchmark` ili `apps` polja."""
    if prev_entry is None:
        return (incoming_ts, dict(incoming_rec))

    prev_ts, prev_rec = prev_entry
    if incoming_ts >= prev_ts:
        older, newer = prev_rec, incoming_rec
        best_ts = incoming_ts
    else:
        older, newer = incoming_rec, prev_rec
        best_ts = prev_ts

    merged = dict(older)
    for k, v in newer.items():
        if isinstance(v, dict):
            old_v = merged.get(k)
            if isinstance(old_v, dict) and old_v:
                if not v:
                    continue
                sub = dict(old_v)
                for sk, sv in v.items():
                    if sv not in (None, "", []):
                        sub[sk] = sv
                merged[k] = sub
            elif v:
                merged[k] = dict(v)
        elif k == "duration_seconds":
            old_d = _safe_float(merged.get("duration_seconds"), 0.0)
            new_d = _safe_float(v, 0.0)
            merged[k] = new_d if new_d > 0 else old_d
        elif k == "hwtest":
            if v:
                merged[k] = v
        elif v not in (None, ""):
            merged[k] = v

    return (best_ts, merged)


def _is_successful_installation(rec: dict) -> bool:
    handover = compute_handover_status(rec)
    return handover in ("READY", "READY_WITH_NOTE")


def compute_fleet_benchmark_summary(fleet_store: Any = None, reports_dir: Optional[str] = None) -> dict:
    """
    Računa agregirani WIS Benchmark pregled za celu radioničku flotu.
    """
    records_by_id: Dict[str, tuple[float, dict]] = {}

    if fleet_store is not None and hasattr(fleet_store, "fleet_devices"):
        lock = getattr(fleet_store, "fleet_lock", None)
        if lock:
            with lock:
                items_snapshot = list(fleet_store.fleet_devices.items())
        else:
            items_snapshot = list(fleet_store.fleet_devices.items())

        for dev_key, raw_rec in items_snapshot:
            if not isinstance(raw_rec, dict):
                continue
            merged = dict(raw_rec.get("latest_report") or {})
            for k, v in raw_rec.items():
                if k != "latest_report" and (k not in merged or merged[k] in (None, "")):
                    merged[k] = v
            if not _is_finished_record(merged):
                continue
            dev_id = sanitize_id(str(merged.get("device_id") or merged.get("computer_name") or dev_key))
            ts_score = _parse_ts_score(merged, _safe_float(raw_rec.get("last_seen"), 0.0))
            records_by_id[dev_id] = _merge_report_records(records_by_id.get(dev_id), ts_score, merged)

    comp_to_dev_id: Dict[str, str] = {}
    serial_to_dev_id: Dict[str, str] = {}
    for did, (_, rec) in records_by_id.items():
        cn = str(rec.get("computer_name") or "").strip().upper()
        sn = str(rec.get("serial") or rec.get("serial_number") or (rec.get("hw") or {}).get("serial") or "").strip().upper()
        if cn:
            comp_to_dev_id[cn] = did
        if sn:
            serial_to_dev_id[sn] = did

    if reports_dir and os.path.isdir(reports_dir):
        try:
            for fname in sorted(os.listdir(reports_dir)):
                if not fname.lower().endswith(".json"):
                    continue
                fpath = os.path.join(reports_dir, fname)
                if not os.path.isfile(fpath):
                    continue
                try:
                    with open(fpath, "r", encoding="utf-8") as f:
                        data = json.load(f)
                    if not isinstance(data, dict) or not _is_finished_record(data):
                        continue
                    raw_did = str(data.get("device_id") or "").strip()
                    cn = str(data.get("computer_name") or "").strip().upper()
                    sn = str(data.get("serial") or data.get("serial_number") or (data.get("hw") or {}).get("serial") or "").strip().upper()
                    if raw_did:
                        dev_id = sanitize_id(raw_did)
                    elif sn and sn in serial_to_dev_id:
                        dev_id = serial_to_dev_id[sn]
                    elif cn and cn in comp_to_dev_id:
                        dev_id = comp_to_dev_id[cn]
                    else:
                        dev_id = sanitize_id(str(data.get("computer_name") or fname[:-5]))

                    if cn and raw_did:
                        comp_to_dev_id[cn] = dev_id
                        old_comp_key = sanitize_id(cn)
                        if old_comp_key != dev_id and old_comp_key in records_by_id:
                            old_ts, old_rec = records_by_id.pop(old_comp_key)
                            records_by_id[dev_id] = _merge_report_records(records_by_id.get(dev_id), old_ts, old_rec)
                    if sn and raw_did:
                        serial_to_dev_id[sn] = dev_id

                    mtime = os.path.getmtime(fpath)
                    ts_score = _parse_ts_score(data, mtime)
                    records_by_id[dev_id] = _merge_report_records(records_by_id.get(dev_id), ts_score, data)
                except Exception:
                    continue
        except Exception:
            pass

    total = len(records_by_id)
    successful = 0
    durations: List[float] = []
    phase_buckets: Dict[str, List[float]] = {pk: [] for pk in PHASE_KEYS}

    total_hits = 0
    total_misses = 0
    total_lan_bytes = 0
    total_wan_bytes = 0
    lan_mbps_samples: List[float] = []
    wan_mbps_samples: List[float] = []

    model_groups: Dict[str, dict] = {}
    recent_runs: List[dict] = []
    v3_total = 0
    v3_successful = 0
    v3_durations: List[float] = []
    legacy_total = 0
    legacy_successful = 0
    legacy_durations: List[float] = []
    op_wait_samples: List[float] = []
    active_hw_samples: List[float] = []

    alias_map = _build_model_alias_map()
    sorted_records = sorted(records_by_id.items(), key=lambda x: x[1][0], reverse=True)

    for dev_id, (ts_score, rec) in sorted_records:
        bm = extract_device_benchmark(rec)
        handover_st = compute_handover_status(rec)
        ok = handover_st in ("READY", "READY_WITH_NOTE")
        if ok:
            successful += 1

        dur = _safe_float(bm.get("total_duration_sec"), 0.0)
        if dur > 0:
            durations.append(dur)

        is_v3_modern = bool(bm.get("has_benchmark") or any(_safe_float(v, 0.0) > 0.0 for v in bm["phase_durations_sec"].values()))
        if is_v3_modern:
            v3_total += 1
            if ok:
                v3_successful += 1
            if dur > 0:
                v3_durations.append(dur)
        else:
            legacy_total += 1
            if ok:
                legacy_successful += 1
            if dur > 0:
                legacy_durations.append(dur)

        if bm.get("operator_wait_sec") is not None:
            op_wait_samples.append(bm["operator_wait_sec"])
        if bm.get("active_hwtest_sec") is not None:
            active_hw_samples.append(bm["active_hwtest_sec"])

        for pk in PHASE_KEYS:
            pv = _safe_float(bm["phase_durations_sec"].get(pk), 0.0)
            if pv > 0:
                phase_buckets[pk].append(pv)

        total_hits += bm["cache_hits"]
        total_misses += bm["cache_misses"]
        total_lan_bytes += bm["lan_bytes"]
        total_wan_bytes += bm["wan_bytes"]
        if bm["lan_throughput_mbps"] > 0:
            lan_mbps_samples.append(bm["lan_throughput_mbps"])
        if bm["wan_throughput_mbps"] > 0:
            wan_mbps_samples.append(bm["wan_throughput_mbps"])

        hw = rec.get("hw") if isinstance(rec.get("hw"), dict) else (rec.get("hardware") if isinstance(rec.get("hardware"), dict) else {})
        raw_model = str(hw.get("model") or rec.get("device_model") or rec.get("model") or "Nepoznat Model").strip()
        raw_mfg = str(hw.get("manufacturer") or "").strip()
        model_label = _resolve_canonical_model_label(raw_mfg, raw_model, alias_map=alias_map)

        mg = model_groups.setdefault(model_label, {
            "model": model_label,
            "count": 0,
            "success_count": 0,
            "durations": [],
            "hits": 0,
            "misses": 0,
        })
        mg["count"] += 1
        if ok:
            mg["success_count"] += 1
        if dur > 0:
            mg["durations"].append(dur)
        mg["hits"] += bm["cache_hits"]
        mg["misses"] += bm["cache_misses"]

        if len(recent_runs) < 20:
            raw_run_st = str(rec.get("status") or "").strip()
            if not ok and raw_run_st.upper() in ("", "SUCCESS", "COMPLETED", "OK", "DONE"):
                effective_run_st = "NEEDS_ATTENTION"
            elif raw_run_st:
                effective_run_st = raw_run_st
            else:
                effective_run_st = "COMPLETED" if ok else "NEEDS_ATTENTION"
            recent_runs.append({
                "device_id": dev_id,
                "computer_name": rec.get("computer_name") or dev_id,
                "model": model_label,
                "status": effective_run_st,
                "handover_status": handover_st,
                "success": ok,
                "duration_seconds": bm["total_duration_sec"],
                "cache_hit_ratio_pct": bm["cache_hit_ratio_pct"],
                "lan_throughput_mbps": bm["lan_throughput_mbps"],
                "timestamp": rec.get("timestamp") or "",
                "phase_durations_sec": bm["phase_durations_sec"],
                "operator_wait_sec": bm["operator_wait_sec"],
                "active_hwtest_sec": bm["active_hwtest_sec"],
            })

    sorted_durs = sorted(durations)
    avg_dur = round(sum(durations) / len(durations), 1) if durations else 0.0
    p50_dur = _percentile(sorted_durs, 50.0)
    p90_dur = _percentile(sorted_durs, 90.0)

    phase_averages = {}
    bottleneck_phase = "N/A"
    bottleneck_val = 0.0
    for pk in PHASE_KEYS:
        vals = phase_buckets[pk]
        avg_p = round(sum(vals) / len(vals), 1) if vals else 0.0
        phase_averages[pk] = avg_p
        if avg_p > bottleneck_val:
            bottleneck_val = avg_p
            bottleneck_phase = pk

    total_reqs = total_hits + total_misses
    hit_ratio_pct = round((total_hits / float(total_reqs)) * 100.0, 1) if total_reqs > 0 else None

    total_bytes = total_lan_bytes + total_wan_bytes
    byte_hit_ratio_pct = round((total_lan_bytes / float(total_bytes)) * 100.0, 1) if total_bytes > 0 else None
    saved_wan_mb = round(total_lan_bytes / (1024.0 * 1024.0), 2)

    avg_lan_mbps = round(sum(lan_mbps_samples) / len(lan_mbps_samples), 2) if lan_mbps_samples else 0.0
    avg_wan_mbps = round(sum(wan_mbps_samples) / len(wan_mbps_samples), 2) if wan_mbps_samples else 0.0

    by_model = []
    for mg in model_groups.values():
        m_durs = mg["durations"]
        m_reqs = mg["hits"] + mg["misses"]
        by_model.append({
            "model": mg["model"],
            "count": mg["count"],
            "avg_duration_seconds": round(sum(m_durs) / len(m_durs), 1) if m_durs else 0.0,
            "success_rate_pct": round((mg["success_count"] / float(mg["count"])) * 100.0, 1) if mg["count"] > 0 else 0.0,
            "cache_hit_ratio_pct": round((mg["hits"] / float(m_reqs)) * 100.0, 1) if m_reqs > 0 else None,
        })
    by_model.sort(key=lambda x: (-x["count"], x["model"].lower()))

    success_rate_pct = round((successful / float(total)) * 100.0, 1) if total > 0 else 0.0
    sorted_v3_durs = sorted(v3_durations)
    sorted_leg_durs = sorted(legacy_durations)

    return {
        "status": "ok",
        "total_installations": total,
        "successful_installations": successful,
        "success_rate_pct": success_rate_pct,
        "avg_duration_seconds": avg_dur,
        "p50_duration_seconds": p50_dur,
        "p90_duration_seconds": p90_dur,
        "phase_averages_sec": phase_averages,
        "bottleneck_phase": bottleneck_phase,
        "cohorts": {
            "v3_modern": {
                "total": v3_total,
                "successful": v3_successful,
                "success_rate_pct": round((v3_successful / float(v3_total)) * 100.0, 1) if v3_total > 0 else None,
                "avg_duration_seconds": round(sum(v3_durations) / len(v3_durations), 1) if v3_durations else 0.0,
                "p50_duration_seconds": _percentile(sorted_v3_durs, 50.0),
                "p90_duration_seconds": _percentile(sorted_v3_durs, 90.0),
            },
            "legacy": {
                "total": legacy_total,
                "successful": legacy_successful,
                "success_rate_pct": round((legacy_successful / float(legacy_total)) * 100.0, 1) if legacy_total > 0 else None,
                "avg_duration_seconds": round(sum(legacy_durations) / len(legacy_durations), 1) if legacy_durations else 0.0,
                "p50_duration_seconds": _percentile(sorted_leg_durs, 50.0),
                "p90_duration_seconds": _percentile(sorted_leg_durs, 90.0),
            },
        },
        "phase5_timing": {
            "samples": len(op_wait_samples),
            "avg_operator_wait_seconds": round(sum(op_wait_samples) / len(op_wait_samples), 1) if op_wait_samples else None,
            "avg_active_hwtest_seconds": round(sum(active_hw_samples) / len(active_hw_samples), 1) if active_hw_samples else None,
        },
        "cache_metrics": {
            "total_hits": total_hits,
            "total_misses": total_misses,
            "cache_hit_ratio_pct": hit_ratio_pct,
            "total_lan_bytes": total_lan_bytes,
            "total_wan_bytes": total_wan_bytes,
            "byte_hit_ratio_pct": byte_hit_ratio_pct,
            "saved_wan_mb": saved_wan_mb,
            "avg_lan_throughput_mbps": avg_lan_mbps,
            "avg_wan_throughput_mbps": avg_wan_mbps,
        },
        "by_model": by_model,
        "recent_runs": recent_runs,
    }
