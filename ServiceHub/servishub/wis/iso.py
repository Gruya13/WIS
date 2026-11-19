"""
servishub.wis.iso
=================
Upravljanje WIS ISO slikama i njihovom ekstrakcijom na pripremljene USB medije.
"""

from datetime import datetime, timezone
import hashlib
import logging
import os
import re
import shutil
import subprocess
import tempfile
import time
from typing import Any, Callable, Dict, List, Optional

from servishub.config import active_config

logger = logging.getLogger("servishub.wis.iso")


def extract_iso_version(filename: str) -> str:
    """Izdvaja verziju iz imena ISO datoteke (npr. wis-v2.1.0.iso -> 2.1.0)."""
    match = re.search(r"v?(\d+(?:\.\d+)+)", filename, re.IGNORECASE)
    if match:
        return match.group(1)
    date_match = re.search(r"(\d{4}[-_]\d{2}[-_]\d{2})", filename)
    if date_match:
        return date_match.group(1).replace("_", "-")
    return "1.0.0"


def calculate_sha256(filepath: str, chunk_size: int = 1024 * 1024) -> str:
    """Računa SHA-256 heš za dati fajl u blokovima."""
    hasher = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(chunk_size):
            hasher.update(chunk)
    return hasher.hexdigest().lower()


def get_cached_sha256(iso_path: str, stat_info: os.stat_result) -> Optional[str]:
    """
    Proverava da li postoji važeći .sha256 prateći fajl.
    Vraća keširani heš ako je fajl noviji ili jednak datumu izmene ISO fajla.
    """
    sha256_path = f"{iso_path}.sha256"
    if not os.path.isfile(sha256_path):
        return None

    try:
        sha_mtime = os.path.getmtime(sha256_path)
        if sha_mtime >= stat_info.st_mtime:
            with open(sha256_path, "r", encoding="utf-8") as f:
                content = f.read().strip()
            if content:
                tokens = content.split()
                if tokens and len(tokens[0]) == 64:
                    return tokens[0].lower()
    except Exception as e:
        logger.debug(f"Greška pri čitanju .sha256 keša ({sha256_path}): {e}")

    return None


def write_cached_sha256(iso_path: str, sha256_hash: str) -> None:
    """Zapisuje izračunati SHA-256 u prateći .sha256 fajl."""
    sha256_path = f"{iso_path}.sha256"
    try:
        with open(sha256_path, "w", encoding="utf-8") as f:
            f.write(f"{sha256_hash}  {os.path.basename(iso_path)}\n")
    except Exception as e:
        logger.warning(f"Neuspešan upis .sha256 keša ({sha256_path}): {e}")


class WisIsoManager:
    """
    Menadžer za pronalaženje, verifikaciju i raspakivanje WIS ISO slika.
    """

    def __init__(
        self,
        wis_dir: Optional[str] = None,
        dry_run: bool = False,
        run_cmd: Optional[Callable] = None,
    ):
        self.wis_dir = wis_dir or getattr(active_config, "wis_dir", os.path.join(active_config.storage_dir, "wis"))
        self.dry_run = dry_run
        self.run_cmd = run_cmd or subprocess.run

    def get_iso_info(self, iso_path: Optional[str] = None) -> Dict[str, Any]:
        """
        Vraća metapodatke o WIS ISO datoteci:
        available, filename, path, size_bytes, size_mb, sha256, modified_at, version.
        
        Ako iso_path nije prosleđen, pretražuje podrazumevani wis_dir folder
        i bira najnoviju .iso sliku.
        Kešira SHA-256 proračun u .sha256 prateći fajl radi brzine.
        """
        resolved_path = iso_path

        # Automatska detekcija ISO slike u wis_dir
        if not resolved_path:
            if os.path.isdir(self.wis_dir):
                candidates: List[str] = []
                for entry in os.listdir(self.wis_dir):
                    if entry.lower().endswith(".iso"):
                        candidates.append(os.path.join(self.wis_dir, entry))
                if candidates:
                    # Sortiraj po vremenu modifikacije opadajuće (najnoviji prvi)
                    candidates.sort(key=lambda p: os.path.getmtime(p), reverse=True)
                    resolved_path = candidates[0]

        if not resolved_path or not os.path.exists(resolved_path):
            return {
                "available": False,
                "filename": os.path.basename(resolved_path) if resolved_path else None,
                "path": os.path.abspath(resolved_path) if resolved_path else None,
                "size_bytes": 0,
                "size_mb": 0.0,
                "sha256": None,
                "modified_at": None,
                "version": None,
            }

        try:
            stat_info = os.stat(resolved_path)
            size_bytes = stat_info.st_size
            size_mb = round(size_bytes / (1024 * 1024), 2)
            modified_dt = datetime.fromtimestamp(stat_info.st_mtime, timezone.utc)
            modified_at = modified_dt.isoformat()
            filename = os.path.basename(resolved_path)
            version = extract_iso_version(filename)

            # Čitanje ili računanje SHA-256 heša
            sha256 = get_cached_sha256(resolved_path, stat_info)
            if not sha256:
                sha256 = calculate_sha256(resolved_path)
                write_cached_sha256(resolved_path, sha256)

            return {
                "available": True,
                "filename": filename,
                "path": os.path.abspath(resolved_path),
                "size_bytes": size_bytes,
                "size_mb": size_mb,
                "sha256": sha256,
                "modified_at": modified_at,
                "version": version,
            }
        except Exception as e:
            logger.error(f"Greška pri čitanju ISO informacija ({resolved_path}): {e}")
            return {
                "available": False,
                "filename": os.path.basename(resolved_path),
                "path": os.path.abspath(resolved_path),
                "size_bytes": 0,
                "size_mb": 0.0,
                "sha256": None,
                "modified_at": None,
                "version": None,
                "error": str(e),
            }

    def extract_iso_to_usb(
        self,
        iso_path: str,
        target_usb_path: str,
        progress_callback: Optional[Callable[[Dict[str, Any]], None]] = None,
    ) -> Dict[str, Any]:
        """
        Raspakuje sadržaj ISO slike na zadatu USB putanju (direktorijum ili particiju).
        
        Prati napredak u opsegu od 0 do 100% preko progress_callback.
        Podržava 7z, loop mount i simulaciju za test okruženje.
        """
        if not os.path.isfile(iso_path):
            raise FileNotFoundError(f"ISO datoteka nije pronađena: {iso_path}")

        def emit(phase: str, percent: int, message: str, **kwargs) -> None:
            if progress_callback:
                payload = {
                    "phase": phase,
                    "percent": percent,
                    "message": message,
                    "iso_path": iso_path,
                    "target_path": target_usb_path,
                }
                payload.update(kwargs)
                progress_callback(payload)

        emit("preparing", 0, f"Priprema za ekstrakciju ISO slike {os.path.basename(iso_path)}...")

        # Provera da li je target_usb_path blok uređaj (npr. /dev/sdb2)
        is_block_device = target_usb_path.startswith("/dev/")
        target_dir = target_usb_path
        temp_target_mount: Optional[str] = None

        if is_block_device:
            emit("mounting_target", 5, f"Povezivanje ciljnog USB uređaja {target_usb_path}...")
            # Proveri da li je već montiran
            existing_mount = self._find_mountpoint_for_device(target_usb_path)
            if existing_mount:
                target_dir = existing_mount
            else:
                temp_target_mount = tempfile.mkdtemp(prefix="wis_target_")
                target_dir = temp_target_mount
                if not self.dry_run:
                    self._execute_mount(target_usb_path, temp_target_mount)

        os.makedirs(target_dir, exist_ok=True)

        try:
            if self.dry_run:
                # Simulacija progresa
                emit("extracting", 25, "Ekstrakcija datoteka (simulacija 25%)...", current_file="bootmgr")
                time.sleep(0.05)
                emit("extracting", 50, "Ekstrakcija datoteka (simulacija 50%)...", current_file="sources/boot.wim")
                time.sleep(0.05)
                emit("extracting", 80, "Ekstrakcija datoteka (simulacija 80%)...", current_file="sources/install.wim")
                time.sleep(0.05)
                emit("syncing", 95, "Sinhronizacija promena na USB disk (sync)...")
                emit("completed", 100, "Ekstrakcija uspešno završena (DRY-RUN).")
                return {
                    "status": "success",
                    "iso_path": iso_path,
                    "target_path": target_usb_path,
                    "target_dir": target_dir,
                    "files_extracted": 3,
                    "dry_run": True,
                }

            # Strategija 1: 7z / 7zz alat
            seven_zip = shutil.which("7z") or shutil.which("7zz") or shutil.which("7za")
            if seven_zip:
                emit("extracting", 15, f"Pokretanje 7-Zip ekstrakcije ({seven_zip})...")
                self._extract_via_7z(seven_zip, iso_path, target_dir, emit)
            else:
                # Strategija 2: loop mount
                emit("extracting", 15, "Montiranje ISO slike preko loop uređaja...")
                self._extract_via_loop_mount(iso_path, target_dir, emit)

            emit("syncing", 95, "Sinhronizacija bafera na USB fleš disk (sync)...")
            if shutil.which("sync"):
                self.run_cmd(["sync"], check=False)

            emit("completed", 100, f"Ekstrakcija ISO slike na {target_usb_path} je uspešno završena.")
            return {
                "status": "success",
                "iso_path": iso_path,
                "target_path": target_usb_path,
                "target_dir": target_dir,
            }

        finally:
            if temp_target_mount:
                try:
                    if shutil.which("umount"):
                        self.run_cmd(["umount", "-l", temp_target_mount], check=False)
                    shutil.rmtree(temp_target_mount, ignore_errors=True)
                except Exception as e:
                    logger.warning(f"Greška pri demontiranju {temp_target_mount}: {e}")

    def _find_mountpoint_for_device(self, dev_path: str) -> Optional[str]:
        """Pronalazi postojeću tačku montiranja u /proc/mounts za dati uređaj."""
        try:
            with open("/proc/mounts", "r", encoding="utf-8") as f:
                for line in f:
                    tokens = line.split()
                    if tokens and tokens[0] == dev_path and len(tokens) >= 2:
                        return tokens[1]
        except Exception:
            pass
        return None

    def _execute_mount(self, device_path: str, mount_point: str) -> None:
        """Pokušava da montira blok uređaj na zadatu tačku."""
        cmd = ["mount", device_path, mount_point]
        res = self.run_cmd(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=False)
        if res.returncode != 0:
            err = res.stderr.strip() if res.stderr else f"Exit code {res.returncode}"
            raise RuntimeError(f"Neuspešno montiranje {device_path} na {mount_point}: {err}")

    def _extract_via_7z(
        self,
        tool: str,
        iso_path: str,
        target_dir: str,
        emit_fn: Callable[..., None],
    ) -> None:
        """Ekstrakcija pomoću 7z arhivera."""
        cmd = [tool, "x", iso_path, f"-o{target_dir}", "-y"]
        res = self.run_cmd(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            check=False,
        )
        if res.returncode != 0:
            err = res.stderr.strip() if res.stderr else f"Exit code {res.returncode}"
            raise RuntimeError(f"Greška pri 7z raspakivanju ISO slike: {err}")
        emit_fn("extracting", 85, "Sve datoteke uspešno raspakovane iz ISO arhive.")

    def _extract_via_loop_mount(
        self,
        iso_path: str,
        target_dir: str,
        emit_fn: Callable[..., None],
    ) -> None:
        """Ekstrakcija montiranjem ISO-a u loop režimu i kopiranjem fajlova uz praćenje progresa."""
        temp_iso_mount = tempfile.mkdtemp(prefix="wis_iso_src_")
        try:
            # Mount ISO
            mount_cmd = ["mount", "-o", "loop,ro", iso_path, temp_iso_mount]
            res = self.run_cmd(mount_cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=False)
            if res.returncode != 0:
                err = res.stderr.strip() if res.stderr else f"Exit code {res.returncode}"
                raise RuntimeError(
                    f"Nije moguće montirati ISO sliku (potrebne su root privilegije ili '7z'): {err}"
                )

            # Popis fajlova za procenu progresa
            all_files: List[tuple[str, int]] = []
            total_bytes = 0
            for root, _, files in os.walk(temp_iso_mount):
                for f in files:
                    src_f = os.path.join(root, f)
                    try:
                        sz = os.path.getsize(src_f)
                    except OSError:
                        sz = 0
                    all_files.append((src_f, sz))
                    total_bytes += sz

            copied_bytes = 0
            total_count = len(all_files)
            for idx, (src_f, sz) in enumerate(all_files, start=1):
                rel_path = os.path.relpath(src_f, temp_iso_mount)
                dst_f = os.path.join(target_dir, rel_path)
                os.makedirs(os.path.dirname(dst_f), exist_ok=True)
                shutil.copy2(src_f, dst_f)
                copied_bytes += sz

                pct = 20 + int((copied_bytes / (total_bytes or 1)) * 70)
                pct = min(pct, 90)
                emit_fn(
                    "extracting",
                    pct,
                    f"Kopiranje {rel_path} ({idx}/{total_count})...",
                    current_file=rel_path,
                    copied_bytes=copied_bytes,
                    total_bytes=total_bytes,
                )
        finally:
            if shutil.which("umount"):
                self.run_cmd(["umount", "-l", temp_iso_mount], check=False)
            shutil.rmtree(temp_iso_mount, ignore_errors=True)


def get_wis_iso_info(iso_path: Optional[str] = None) -> Dict[str, Any]:
    """Pomoćna funkcija za brzo dobijanje informacija o WIS ISO slici."""
    manager = WisIsoManager()
    return manager.get_iso_info(iso_path=iso_path)
