# -*- coding: utf-8 -*-
"""
iso_builder.py - Asinhroni upravljač izgradnjom Ventoy Windows 11 ISO slike
Deo GWADT USB Tool backend arhitekture.
Omogućava pokretanje izgradnje u pozadinskoj niti i praćenje statusa preko REST API-ja.
Podržava hibridne xorriso motore ('auto', 'host', 'docker'). genisoimage je trajno uklonjen.
GWADT-TASK-050: Delegira staging proceduru IsoBuilder klasi uz obavezno uključivanje
folder strukture sources/$OEM$/$1/setup/Programi (4.0 GB offline instalacionih paketa).
"""

import os
import sys
import time
import threading
import importlib.util
from pathlib import Path
from typing import Optional, Dict, Any, Tuple

# Određivanje korena projekta
CORE_DIR = Path(__file__).resolve().parent
TOOL_DIR = CORE_DIR.parent
PROJECT_ROOT = Path(os.environ.get("GWADT_PROJECT_ROOT", str(TOOL_DIR.parent))).resolve()

BUILD_ISO_SCRIPT = PROJECT_ROOT / "Skripte" / "Build-Iso.py"


def _load_build_iso_module():
    """Dinamički učitava modul Skripte/Build-Iso.py."""
    if not BUILD_ISO_SCRIPT.exists():
        raise FileNotFoundError(f"Skripta {BUILD_ISO_SCRIPT} nije pronađena!")
    spec = importlib.util.spec_from_file_location("gwadt_build_iso", str(BUILD_ISO_SCRIPT))
    if spec is None or spec.loader is None:
        raise ImportError(f"Nije moguće kreirati modul za {BUILD_ISO_SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class IsoBuildManager:
    """
    Thread-safe menadžer za asinhronu izgradnju ISO slike.
    Podržava praćenje napretka, prikupljanje logova i očitavanje stanja.
    """

    def __init__(self, project_root: Optional[Path] = None):
        self.project_root = Path(project_root or PROJECT_ROOT).resolve()
        self.lock = threading.Lock()
        self.worker_thread: Optional[threading.Thread] = None

        self.state: Dict[str, Any] = {
            "active": False,
            "status": "idle",  # 'idle', 'staging', 'building', 'done', 'error'
            "progress": 0,
            "step": "Spremno za rad",
            "output_file": "",
            "iso_size_mb": 0.0,
            "iso_size_gb": 0.0,
            "sha256": "",
            "md5": "",
            "error": None,
            "start_time": 0.0,
            "duration": 0.0,
            "logs": []
        }

    def get_status(self) -> Dict[str, Any]:
        """Vraća kopiju trenutnog stanja procesa."""
        with self.lock:
            st = dict(self.state)
            st["logs"] = list(self.state["logs"])
            if st["active"] and st["start_time"] > 0:
                st["duration"] = round(time.time() - st["start_time"], 1)
            return st

    def _log(self, text: str):
        """Dodaje zapis u interni log bafer uz održavanje maksimalne veličine."""
        timestamp = time.strftime("%H:%M:%S")
        entry = f"[{timestamp}] {text}"
        with self.lock:
            self.state["logs"].append(entry)
            if len(self.state["logs"]) > 250:
                self.state["logs"].pop(0)

    def _update_state(self, **kwargs):
        """Ažurira stanje procesa pod lock-om."""
        with self.lock:
            for k, v in kwargs.items():
                if k in self.state:
                    self.state[k] = v

    def start_build(
        self,
        output_dir: Optional[str] = None,
        profile: str = "lean",
        no_prompt: bool = True,
        force: bool = False,
        engine: str = "auto",
        dry_run: bool = False
    ) -> Tuple[bool, str]:
        """
        Pokreće proces izgradnje ISO slike u zasebnoj niti.
        Vraća (uspeh, poruka).
        """
        engine_clean = (engine or "auto").strip().lower()
        allowed_engines = ["auto", "genisoimage", "udf", "mkisofs", "host", "docker", "host_xorriso", "docker_xorriso", "xorriso"]
        if engine_clean not in allowed_engines:
            return False, f"Nepoznat ISO motor '{engine}'. Podržani motori su: 'auto', 'genisoimage' (UDF), 'host' (xorriso), 'docker'."

        with self.lock:
            if self.state["active"]:
                return False, "Proces izgradnje ISO slike je već u toku!"

            self.state = {
                "active": True,
                "status": "staging",
                "progress": 5,
                "profile": profile,
                "step": "Inicijalizacija i validacija izvora...",
                "output_file": "",
                "iso_size_mb": 0.0,
                "iso_size_gb": 0.0,
                "sha256": "",
                "md5": "",
                "error": None,
                "start_time": time.time(),
                "duration": 0.0,
                "logs": []
            }

        self.worker_thread = threading.Thread(
            target=self._worker,
            args=(output_dir, profile, no_prompt, force, engine, dry_run),
            daemon=True
        )
        self.worker_thread.start()
        return True, "Izgradnja ISO slike je uspešno pokrenuta u pozadini."

    def _worker(
        self,
        output_dir: Optional[str],
        profile: str,
        no_prompt: bool,
        force: bool,
        engine: str,
        dry_run: bool = False
    ):
        """Pozadinska nit koja komunicira sa IsoBuilder motorom."""
        start_ts = time.time()
        self._log(f"Započeta pozadinska procedura generisanja ISO slike (profile={profile}, dry_run={dry_run}).")

        try:
            mod = _load_build_iso_module()
            IsoBuilderClass = getattr(mod, "IsoBuilder")

            out_path = Path(output_dir) if output_dir else None

            builder = IsoBuilderClass(
                project_root=self.project_root,
                output_path=out_path,
                profile=profile,
                no_prompt=no_prompt,
                force=force,
                dry_run=dry_run,
                engine=engine
            )

            self._update_state(output_file=str(builder.output_path))
            self._log(f"Ciljna lokacija ISO fajla: {builder.output_path}")

            def _progress_cb(data: Dict[str, Any]):
                step = data.get("step", "")
                prog = data.get("progress", 0)
                status_raw = data.get("status", "running")

                status_mapped = "building"
                if prog < 25:
                    status_mapped = "staging"
                elif status_raw == "done":
                    status_mapped = "done"
                elif status_raw == "error":
                    status_mapped = "error"

                self._update_state(
                    step=step,
                    progress=prog,
                    status=status_mapped
                )

            def _log_cb(msg: str):
                self._log(msg)

            # Izvršavanje izgradnje
            result = builder.build_iso(progress_cb=_progress_cb, log_cb=_log_cb)

            duration = round(time.time() - start_ts, 1)
            is_dry = bool(result.get("dry_run", False))
            done_msg = "Dry-Run simulacija uspešna." if is_dry else "ISO slika je uspešno generisana i spremna za Ventoy."
            self._update_state(
                active=False,
                status="done",
                progress=100,
                step=done_msg,
                output_file=result.get("output_path", str(builder.output_path)),
                iso_size_mb=result.get("iso_size_mb", 0.0),
                iso_size_gb=result.get("iso_size_gb", 0.0),
                md5=result.get("md5", ""),
                sha256=result.get("sha256", ""),
                duration=duration,
                error=None
            )
            self._log(f"Procedura uspešno okončana za {duration} s. ISO: {builder.output_path}")

        except Exception as e:
            err_msg = str(e)
            duration = round(time.time() - start_ts, 1)
            self._log(f"[GRESKA] Neuspeh izgradnje ISO slike: {err_msg}")
            self._update_state(
                active=False,
                status="error",
                step=f"Greška: {err_msg}",
                error=err_msg,
                duration=duration
            )


# Globalna deljena instanca za USB Tool server
GLOBAL_ISO_BUILD_MANAGER = IsoBuildManager()
