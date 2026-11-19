# -*- coding: utf-8 -*-
"""
usb_sync.py - Motor za nadogradnju USB fleša, izolovani build i sinhronizaciju sistema (WIS v2.16.0)
Implementira:
- Transakcionu WIM/ESD zamenu preko privremenog fajla sa SHA-256 i WIM metadata verifikacijom i rollback-om (Nalaz 6)
- Izolovani radni direktorijum po pokušaju i strogu proveru exit code-a za online build (Nalaz 7)
- Jedinstveni kanonski deployment manifest sa pre-check i post-copy SHA-256 verifikacijom (Nalaz 11)
- Međuprocesno i višenitno zaključavanje staging promocije i USB cilja uz unikatne privremene fajlove (Nalaz 12)
"""

import os
import re
import sys
import uuid
import json
import time
import fcntl
import shutil
import hashlib
import platform
import threading
import subprocess
from contextlib import contextmanager
from pathlib import Path
from .patch_engine import get_staging_dir, get_staging_metadata

# Dodavanje Skripte direktorijuma za uvoz kanonskog manifest_loader modula
_PROJECT_ROOT_DEFAULT = Path(__file__).resolve().parent.parent.parent
_SKRIPTE_DIR = _PROJECT_ROOT_DEFAULT / "Skripte"
if str(_SKRIPTE_DIR) not in sys.path:
    sys.path.insert(0, str(_SKRIPTE_DIR))

from manifest_loader import (
    calc_sha256,
    sync_manifest_to_target,
    validate_target_space_and_fs,
)

# Globalni i međuprocesni lock-ovi za sprečavanje kolizije (Nalaz 12)
STAGING_PROMOTION_LOCK = threading.Lock()
_USB_TARGET_LOCKS_GUARD = threading.Lock()
_USB_TARGET_LOCKS = {}


def _get_project_root():
    return Path(os.environ.get("GWADT_PROJECT_ROOT", str(_PROJECT_ROOT_DEFAULT))).resolve()


@contextmanager
def acquire_staging_lock(staging_dir=None):
    """
    Zajedničko zaključavanje lokalnog Pre-Stage keša (in-process threading.Lock + međuprocesni fcntl.flock).
    Sprečava da ProgramRefreshWorker, OnlineBuildWorker i CLI alati istovremeno prepisuju staging keš.
    """
    sdir = Path(staging_dir) if staging_dir else get_staging_dir()
    sdir.mkdir(parents=True, exist_ok=True)
    lock_file_path = sdir / ".staging.lock"
    with STAGING_PROMOTION_LOCK:
        with open(lock_file_path, "a+", encoding="utf-8") as lf:
            fcntl.flock(lf.fileno(), fcntl.LOCK_EX)
            try:
                yield
            finally:
                try:
                    fcntl.flock(lf.fileno(), fcntl.LOCK_UN)
                except Exception:
                    pass


@contextmanager
def acquire_usb_target_lock(usb_root, non_blocking=False):
    """
    Zaključavanje konkretnog USB odredišta (in-process + međuprocesni lock u /tmp)
    kako GUI i CLI ne bi istovremeno menjali isti USB medijum.
    """
    real_target = os.path.realpath(str(usb_root))
    target_hash = hashlib.sha256(real_target.encode("utf-8")).hexdigest()[:16]
    lock_file_path = Path("/tmp") / f"wis_usb_target_{target_hash}.lock"

    with _USB_TARGET_LOCKS_GUARD:
        t_lock = _USB_TARGET_LOCKS.get(real_target)
        if t_lock is None:
            t_lock = threading.Lock()
            _USB_TARGET_LOCKS[real_target] = t_lock

    acquired_thread = t_lock.acquire(blocking=not non_blocking)
    if not acquired_thread:
        raise RuntimeError(f"Ciljni USB medijum ({usb_root}) je već zauzet drugom operacijom!")

    lf = None
    try:
        lf = open(lock_file_path, "a+", encoding="utf-8")
        flags = fcntl.LOCK_EX | (fcntl.LOCK_NB if non_blocking else 0)
        try:
            fcntl.flock(lf.fileno(), flags)
        except BlockingIOError:
            raise RuntimeError(f"Ciljni USB medijum ({usb_root}) je zaključan od strane drugog procesa!")
        yield
    finally:
        if lf is not None:
            try:
                fcntl.flock(lf.fileno(), fcntl.LOCK_UN)
            except Exception:
                pass
            try:
                lf.close()
            except Exception:
                pass
        t_lock.release()


class UsbUpgradeWorker:
    """
    Pozadinski radnik koji vrši transakcioni prenos WIM/ESD slike i sinhronizaciju skripti na USB.
    Čuva postojeću sliku sve dok nova slika i obavezne skripte nisu u potpunosti potvrđene.
    """
    def __init__(self):
        self.lock = threading.Lock()
        self.state = {
            "status": "idle",  # "idle", "running", "success", "error"
            "percent": 0.0,
            "copied_mb": 0,
            "total_mb": 0,
            "speed_mb_s": 0.0,
            "eta_seconds": 0,
            "current_step": "Spremno za rad",
            "message": "",
            "logs": [],
            "start_time": 0,
            "elapsed_seconds": 0,
        }
        self.thread = None

    def get_progress(self):
        with self.lock:
            return dict(self.state)

    def log(self, text):
        timestamp = time.strftime("%H:%M:%S")
        entry = f"[{timestamp}] {text}"
        with self.lock:
            self.state["logs"].append(entry)
            if len(self.state["logs"]) > 200:
                self.state["logs"].pop(0)
            self.state["message"] = text

    def update_progress(self, percent, copied_mb, total_mb, speed, eta, step):
        with self.lock:
            self.state["percent"] = round(percent, 1)
            self.state["copied_mb"] = copied_mb
            self.state["total_mb"] = total_mb
            self.state["speed_mb_s"] = round(speed, 1)
            self.state["eta_seconds"] = int(eta)
            self.state["current_step"] = step

    def start_upgrade(self, usb_mount_path, source_wim_path=None, force=False):
        """Pokreće proceduru nadogradnje u zasebnoj niti."""
        with self.lock:
            if self.state["status"] == "running":
                return False, "Operacija nadogradnje je već u toku!"

            self.state = {
                "status": "running",
                "percent": 0.0,
                "copied_mb": 0,
                "total_mb": 0,
                "speed_mb_s": 0.0,
                "eta_seconds": 0,
                "current_step": "Inicijalizacija nadogradnje...",
                "message": "Započinjem proces...",
                "logs": [],
                "start_time": time.time(),
                "elapsed_seconds": 0,
            }

        self.thread = threading.Thread(
            target=self._run_upgrade,
            args=(usb_mount_path, source_wim_path, force),
            daemon=True,
        )
        self.thread.start()
        return True, "Nadogradnja je uspešno pokrenuta u pozadini."

    def _run_upgrade(self, usb_mount_path, custom_source_wim, force, fstype_override=None):
        attempt_id = f"{int(time.time())}_{os.getpid()}_{uuid.uuid4().hex[:6]}"
        tmp_img = None
        bak_img = None
        target_usb_img = None
        promoted_new_image = False

        try:
            self.log(f"Započeta nadogradnja na USB medijum: {usb_mount_path} (Attempt: {attempt_id})")
            usb_root = Path(usb_mount_path)
            if not usb_root.exists() or not usb_root.is_dir():
                raise RuntimeError(f"Ciljna putanja USB medijuma ne postoji: {usb_mount_path}")

            with acquire_usb_target_lock(usb_root, non_blocking=True):
                # 0. Pre-check kanonskog manifesta skripti pre diranja slike
                project_root = _get_project_root()
                from manifest_loader import validate_manifest_sources
                validate_manifest_sources(project_root=project_root)

                # 1. Određivanje izvornog WIM/ESD fajla
                src_wim = None
                if custom_source_wim and os.path.isfile(custom_source_wim):
                    src_wim = Path(custom_source_wim)
                else:
                    staging_wim = get_staging_dir() / "install.wim"
                    staging_esd = get_staging_dir() / "install.esd"
                    if staging_wim.exists():
                        src_wim = staging_wim
                    elif staging_esd.exists():
                        src_wim = staging_esd
                    else:
                        proj_wim = project_root / "sources" / "install.wim"
                        proj_esd = project_root / "sources" / "install.esd"
                        if proj_wim.exists():
                            src_wim = proj_wim
                        elif proj_esd.exists():
                            src_wim = proj_esd

                if not src_wim or not src_wim.exists():
                    raise RuntimeError("Nije pronađena pripremljena WIM/ESD slika u programu niti u kešu!")

                from .system_inspector import read_wim_metadata
                src_editions = read_wim_metadata(str(src_wim))
                if not src_editions:
                    raise RuntimeError(f"Izvorna slika '{src_wim.name}' nema validno WIM/ESD zaglavlje ili metapodatke!")

                staging_meta = get_staging_metadata()
                src_pro_ed = next((e for e in src_editions if "Pro" in e.get("name", "")), src_editions[0])
                src_version = src_pro_ed.get("version_str") or staging_meta.get("version_str", "10.0.26200")
                src_size_bytes = src_wim.stat().st_size
                src_size_mb = src_size_bytes // (1024 * 1024)
                self.log(f"Izvorna slika: {src_wim.name} (Build: {src_version}, Veličina: {src_size_mb} MB)")

                # 2. Priprema foldera na USB flešu
                sources_dir = usb_root / "sources"
                sources_dir.mkdir(parents=True, exist_ok=True)
                ext = src_wim.suffix.lower()
                if ext not in (".wim", ".esd"):
                    ext = ".wim"
                target_usb_img = sources_dir / f"install{ext}"
                conflicting_usb_img = sources_dir / ("install.esd" if ext == ".wim" else "install.wim")

                # 3. Stroga provera slobodnog prostora i FAT32 limita BEZ brisanja postojeće slike (Nalaz 6)
                self.update_progress(1.0, 0, src_size_mb, 0, 0, "Provera prostora i fajlsistema na USB disku...")
                space_info = validate_target_space_and_fs(
                    sources_dir,
                    src_size_bytes,
                    reserve_mb=250,
                    fstype_override=fstype_override,
                )
                free_mb = space_info["free_bytes"] // (1024 * 1024)
                needed_mb = space_info["needed_bytes"] // (1024 * 1024)
                self.log(
                    f"Provera prostora OK (FS: {space_info['fstype']}): slobodno {free_mb} MB, "
                    f"potrebno za staging {needed_mb} MB (postojeća slika ostaje zaštićena tokom prenosa)."
                )

                # 4. Prenos u privremeni fajl pokušaja na ciljnom USB-u uz on-the-fly SHA-256
                tmp_img = sources_dir / f".install{ext}.tmp.{attempt_id}"
                self.log(f"Započinjem prenos slike u privremeni staging fajl: {tmp_img.name}...")
                chunk_size = 16 * 1024 * 1024  # 16 MB
                copied_bytes = 0
                start_time = time.time()
                last_ui_update = 0
                src_hasher = hashlib.sha256()

                with open(src_wim, "rb") as f_src, open(tmp_img, "wb") as f_dst:
                    while True:
                        chunk = f_src.read(chunk_size)
                        if not chunk:
                            break
                        src_hasher.update(chunk)
                        f_dst.write(chunk)
                        copied_bytes += len(chunk)

                        now = time.time()
                        if now - last_ui_update >= 0.4 or copied_bytes == src_size_bytes:
                            last_ui_update = now
                            elapsed = max(now - start_time, 0.001)
                            speed = (copied_bytes / (1024 * 1024)) / elapsed
                            percent = (copied_bytes / max(1, src_size_bytes)) * 85.0
                            rem_bytes = max(0, src_size_bytes - copied_bytes)
                            eta = (rem_bytes / (1024 * 1024)) / speed if speed > 0 else 0

                            copied_mb = copied_bytes // (1024 * 1024)
                            self.update_progress(
                                percent,
                                copied_mb,
                                src_size_mb,
                                speed,
                                eta,
                                f"Kopiranje slike u staging: {(copied_bytes / max(1, src_size_bytes)) * 100.0:.1f}% ({speed:.1f} MB/s)",
                            )

                    f_dst.flush()
                    os.fsync(f_dst.fileno())

                expected_sha256 = src_hasher.hexdigest()
                self.log(f"Upis privremene slike završen ({src_size_mb} MB). Verifikujem SHA-256 i WIM strukturu...")

                # 5. Verifikacija privremenog fajla pre bilo kakve zamene postojeće slike
                self.update_progress(88.0, src_size_mb, src_size_mb, 0, 0, "Verifikacija integriteta privremene slike...")
                if not tmp_img.exists() or tmp_img.stat().st_size != src_size_bytes:
                    raise RuntimeError(
                        f"Veličina privremene slike ({tmp_img.stat().st_size if tmp_img.exists() else 0} B) "
                        f"ne odgovara izvoru ({src_size_bytes} B)!"
                    )

                actual_tmp_sha256 = calc_sha256(tmp_img)
                if actual_tmp_sha256 != expected_sha256:
                    raise RuntimeError(
                        f"SHA-256 kontrolna suma privremene slike na USB-u se ne poklapa "
                        f"(očekivano {expected_sha256[:12]}, dobijeno {actual_tmp_sha256[:12]})!"
                    )

                verified_editions = read_wim_metadata(str(tmp_img))
                if not verified_editions:
                    raise RuntimeError("Privremena slika na USB-u nema čitljive WIM/ESD metapodatke!")

                # 6. Transakciona promocija slike uz čuvanje .bak.<attempt_id> za rollback
                self.update_progress(92.0, src_size_mb, src_size_mb, 0, 0, "Atomska promocija verifikovane slike...")
                if target_usb_img.exists():
                    bak_img = sources_dir / f"{target_usb_img.name}.bak.{attempt_id}"
                    self.log(f"Čuvam prethodnu sliku kao sigurnosni backup pokušaja: {bak_img.name}...")
                    os.replace(target_usb_img, bak_img)

                os.replace(tmp_img, target_usb_img)
                promoted_new_image = True
                tmp_img = None

                # 7. Sinhronizacija i SHA-256 verifikacija svih obaveznih deploy skripti po manifestu
                self.update_progress(95.0, src_size_mb, src_size_mb, 0, 0, "Sinhronizacija i verifikacija deploy skripti...")
                self.log("Sinhronizujem i verifikujem deploy skripte prema kanonskom manifestu...")
                self._sync_deploy_scripts(usb_root)

                # 8. Tek kada su i slika i skripte potvrđeni, uklanjamo backup ovog pokušaja i konfliktni stari format
                if bak_img and bak_img.exists():
                    try:
                        bak_img.unlink()
                        self.log(f"Uklonjen privremeni backup {bak_img.name} nakon uspešne verifikacije.")
                    except Exception as be:
                        self.log(f"Napomena pri brisanju backup-a {bak_img.name}: {be}")

                if conflicting_usb_img.exists():
                    try:
                        conflicting_usb_img.unlink()
                        self.log(f"Uklonjen zastareli konfliktni format slike: {conflicting_usb_img.name}.")
                    except Exception as ce:
                        self.log(f"Napomena pri brisanju konfliktnog formata {conflicting_usb_img.name}: {ce}")

                # 9. Sistemski bafer sync
                self.update_progress(98.0, src_size_mb, src_size_mb, 0, 0, "Ispiranje keša na fleš memoriju (sync)...")
                self.log("Pozivam 'sync' kako bi svi baferi bili fizički upisani na fleš...")
                if platform.system() != "Windows":
                    try:
                        subprocess.run(["sync"], timeout=30)
                    except Exception:
                        pass

                elapsed_total = int(time.time() - start_time)
                ver_ed = next((e for e in verified_editions if "Pro" in e.get("name", "")), verified_editions[0])
                final_ver_str = ver_ed.get("version_str", src_version)

                try:
                    sys.stdout.write("\a")
                    sys.stdout.flush()
                except Exception:
                    pass

                with self.lock:
                    self.state["status"] = "success"
                    self.state["percent"] = 100.0
                    self.state["elapsed_seconds"] = elapsed_total
                    self.state["current_step"] = f"Završeno za {elapsed_total}s! USB je 100% ažuran."
                    self.state["message"] = f"Uspešna nadogradnja! Novi Build na USB-u: {final_ver_str}"

                self.log(
                    f"USPEH: USB fleš je kompletno osvežen za {elapsed_total} sekundi "
                    f"(Verifikovan Build: {final_ver_str}, SHA-256: {expected_sha256[:12]})!"
                )

        except Exception as e:
            err_msg = str(e)
            self.log(f"GRESKA tokom nadogradnje: {err_msg}")

            # Čišćenje isključivo privremenog fajla ovog pokušaja
            if tmp_img is not None and tmp_img.exists():
                try:
                    tmp_img.unlink()
                    self.log(f"Očišćen nekompletan privremeni fajl pokušaja: {tmp_img.name}")
                except Exception:
                    pass

            # Rollback prethodne slike ako je bila preimenovana u .bak.<attempt_id>
            if bak_img is not None and bak_img.exists() and target_usb_img is not None:
                try:
                    os.replace(bak_img, target_usb_img)
                    self.log(f"ROLLBACK USPEŠAN: Prethodna ispravna slika ({target_usb_img.name}) je vraćena iz {bak_img.name}!")
                except Exception as rb_err:
                    self.log(f"KRITIČNA GREŠKA pri rollback-u {bak_img.name}: {rb_err}")

            with self.lock:
                self.state["status"] = "error"
                self.state["current_step"] = "Greška tokom operacije"
                self.state["message"] = err_msg

    def _sync_deploy_scripts(self, usb_root):
        """
        Kopira i verifikuje sve produkcione skripte, module, faze i binarce na USB fleš
        prema jedinstvenom kanonskom manifestu (`Skripte/deployment_manifest.json`).
        """
        project_root = _get_project_root()
        return sync_manifest_to_target(
            usb_root=usb_root,
            project_root=project_root,
            log_fn=self.log,
        )


# Globalna instanca radnika za sinhronizaciju
GLOBAL_UPGRADE_WORKER = UsbUpgradeWorker()


def sync_scripts_only(usb_mount_path):
    """
    Brza, atomska sinhronizacija svih skripti, modula, faza i binaraca prema kanonskom manifestu
    bez diranja WIM/ESD slike. Vraća (False, poruka) ako nedostaje obavezna komponenta ili heš ne prolazi.
    """
    try:
        usb_root = Path(usb_mount_path)
        if not usb_root.exists() or not usb_root.is_dir():
            return False, "Ciljna putanja ne postoji ili nije direktorijum."

        with acquire_usb_target_lock(usb_root, non_blocking=True):
            res = GLOBAL_UPGRADE_WORKER._sync_deploy_scripts(usb_root)
            if platform.system() != "Windows":
                try:
                    subprocess.run(["sync"], timeout=10)
                except Exception:
                    pass
            return (
                True,
                f"Sve deploy komponente ({res['files_written']} fajlova) su uspešno osvežene i SHA-256 verifikovane.",
            )
    except Exception as e:
        return False, str(e)


class ProgramRefreshWorker:
    """
    Pozadinski radnik za pripremu i sabijanje nove Windows 11 WIM slike
    direktno u lokalni program / Pre-Stage keš računara (100% nezavisno od USB-a).
    Koristi izolovani privremeni fajl po pokušaju i zajednički lock pri promociji (Nalaz 12).
    """
    def __init__(self):
        self.lock = threading.Lock()
        self.state = {
            "status": "idle",  # "idle", "running", "success", "error"
            "percent": 0.0,
            "current_step": "Spremno",
            "message": "",
            "logs": [],
            "source_used": "",
        }
        self.thread = None

    def get_progress(self):
        with self.lock:
            return dict(self.state)

    def log(self, text):
        timestamp = time.strftime("%H:%M:%S")
        entry = f"[{timestamp}] {text}"
        with self.lock:
            self.state["logs"].append(entry)
            if len(self.state["logs"]) > 200:
                self.state["logs"].pop(0)
            self.state["message"] = text

    def start_prepare(self, source_path_or_url):
        with self.lock:
            if self.state["status"] == "running":
                return False, "Priprema slike u programu je već u toku!"
            self.state = {
                "status": "running",
                "percent": 0.0,
                "current_step": "Inicijalizacija izvora...",
                "message": "Započinjem pripremu u programu...",
                "logs": [],
                "source_used": str(source_path_or_url),
            }

        self.thread = threading.Thread(
            target=self._run_prepare,
            args=(source_path_or_url,),
            daemon=True,
        )
        self.thread.start()
        return True, "Priprema nove slike u programu je pokrenuta u pozadini."

    def _run_prepare(self, source):
        loop_dev = None
        attempt_id = f"{int(time.time())}_{os.getpid()}_{uuid.uuid4().hex[:6]}"
        tmp_wim = None
        try:
            self.log(f"Izvor za pripremu: {source} (Attempt: {attempt_id})")
            staging_dir = get_staging_dir()
            staging_dir.mkdir(parents=True, exist_ok=True)
            target_wim = staging_dir / "install.wim"
            tmp_wim = staging_dir / f"install_building_refresh_{attempt_id}.wim"

            source_p = Path(source) if os.path.exists(source) else None
            source_img = None

            # 1. Ako je lokalni ISO fajl
            if source_p and source_p.suffix.lower() == ".iso":
                self.log(f"Montiram ISO sliku bez sudo privilegija: {source_p.name}...")
                mount_dir, loop_dev = self._mount_iso(source_p)
                source_candidates = [
                    mount_dir / "sources" / "install.wim",
                    mount_dir / "sources" / "install.esd",
                ]
                source_img = next((s for s in source_candidates if s.exists()), None)
                if not source_img:
                    raise RuntimeError("U ISO fajlu nema sources/install.wim ili install.esd!")
            elif source_p and source_p.suffix.lower() in [".wim", ".esd"]:
                source_img = source_p
            else:
                raise RuntimeError(f"Izvor nije pronađen ili nije podržan format (potreban .iso, .wim ili .esd): {source}")

            from .system_inspector import read_wim_metadata
            src_editions = read_wim_metadata(str(source_img))
            if not src_editions:
                raise RuntimeError(f"Izvorna slika '{source_img}' nema validne WIM/ESD metapodatke!")

            # 2. Izvoz Pro edicije u izolovani privremeni fajl pokušaja
            with self.lock:
                self.state["percent"] = 25.0
                self.state["current_step"] = "Izvoz i sabijanje 'Windows 11 Pro' edicije (Solid LZX)..."
            self.log(f"Pokrećem wimlib-imagex export u izolovani fajl {tmp_wim.name}...")

            export_cmd = [
                "wimlib-imagex",
                "export",
                str(source_img),
                "Windows 11 Pro",
                str(tmp_wim),
                "--compress=maximum",
            ]
            res = subprocess.run(export_cmd, capture_output=True, text=True)
            if res.returncode != 0:
                self.log("Pokušavam izvoz po indeksu 1...")
                export_cmd[3] = "1"
                res = subprocess.run(export_cmd, capture_output=True, text=True)
                if res.returncode != 0:
                    raise RuntimeError(f"wimlib-imagex greška (exit {res.returncode}): {res.stderr}")

            # 3. Verifikacija privremenog WIM-a pre zaključane promocije u keš
            with self.lock:
                self.state["percent"] = 85.0
                self.state["current_step"] = "Verifikacija i atomska promocija u keš..."
            self.log("Optimizovani WIM je generisan. Verifikujem metapodatke i SHA-256 pre promocije...")

            if not tmp_wim.exists() or tmp_wim.stat().st_size == 0:
                raise RuntimeError("Izvezeni privremeni WIM fajl ne postoji ili je prazan!")

            editions = read_wim_metadata(str(tmp_wim))
            if not editions:
                raise RuntimeError("Izvezeni privremeni WIM fajl nije prošao proveru WIM metapodataka!")

            pro_ed = editions[0]
            size_bytes = tmp_wim.stat().st_size
            size_mb = size_bytes // (1024 * 1024)
            wim_sha256 = calc_sha256(tmp_wim)

            meta = {
                "edition": pro_ed.get("name", "Windows 11 Pro"),
                "build": pro_ed.get("build", 26200),
                "spbuild": pro_ed.get("spbuild", 8037),
                "version_str": pro_ed.get("version_str", "10.0.26200.8037"),
                "size_bytes": size_bytes,
                "size_mb": size_mb,
                "sha256": wim_sha256,
                "attempt_id": attempt_id,
                "prepared_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            }

            with acquire_staging_lock(staging_dir):
                os.replace(tmp_wim, target_wim)
                tmp_wim = None
                meta_file = staging_dir / "staging_meta.json"
                tmp_meta = staging_dir / f".staging_meta.json.tmp.{attempt_id}"
                with open(tmp_meta, "w", encoding="utf-8") as mf:
                    json.dump(meta, mf, indent=2, ensure_ascii=False)
                    mf.flush()
                    os.fsync(mf.fileno())
                os.replace(tmp_meta, meta_file)

            with self.lock:
                self.state["status"] = "success"
                self.state["percent"] = 100.0
                self.state["current_step"] = f"Pripremljen Build {meta['version_str']} ({size_mb} MB) u kešu!"
                self.state["message"] = "Nova slika je 100% spremna u programu za sledeći USB fleš!"

            self.log(f"USPEH: Program je uspešno ažuriran na Build {meta['version_str']} ({size_mb} MB, SHA-256: {wim_sha256[:12]}).")

        except Exception as e:
            err = str(e)
            self.log(f"GRESKA tokom pripreme slike: {err}")
            if tmp_wim is not None and tmp_wim.exists():
                try:
                    tmp_wim.unlink()
                except Exception:
                    pass
            with self.lock:
                self.state["status"] = "error"
                self.state["current_step"] = "Greška tokom pripreme"
                self.state["message"] = err
        finally:
            if loop_dev:
                self._unmount_iso(loop_dev)

    def _mount_iso(self, iso_path):
        cmd = ["udisksctl", "loop-setup", "-r", "-f", str(iso_path)]
        res = subprocess.run(cmd, capture_output=True, text=True)
        if res.returncode != 0:
            raise RuntimeError(f"Neuspešan loop-setup: {res.stderr}")
        m = re.search(r"(/dev/loop\d+)", res.stdout)
        if not m:
            raise RuntimeError(f"Nije prepoznat loop uređaj: {res.stdout}")
        loop_dev = m.group(1)

        m_res = subprocess.run(["udisksctl", "mount", "-b", loop_dev], capture_output=True, text=True)
        if m_res.returncode != 0:
            subprocess.run(["udisksctl", "loop-delete", "-b", loop_dev], capture_output=True)
            raise RuntimeError(f"Neuspešno montiranje: {m_res.stderr}")

        mm = re.search(r"at\s+(/run/media/\S+|/media/\S+)", m_res.stdout)
        if not mm:
            raise RuntimeError(f"Nije pronađena mount putanja: {m_res.stdout}")
        return Path(mm.group(1).strip(".")), loop_dev

    def _unmount_iso(self, loop_dev):
        try:
            subprocess.run(["udisksctl", "unmount", "-b", loop_dev], capture_output=True)
        except Exception:
            pass
        try:
            subprocess.run(["udisksctl", "loop-delete", "-b", loop_dev], capture_output=True)
        except Exception:
            pass


GLOBAL_PROGRAM_REFRESH_WORKER = ProgramRefreshWorker()


class OnlineBuildWorker:
    """
    Pozadinski radnik koji automatski preuzima i sklapa najnoviji Windows 11 Build:
    - Koristi prazan, izolovan radni direktorijum po pokušaju (Nalaz 7).
    - Zahteva striktan exit code 0 iz uup_download_linux.sh bez fallback-a na stare slike (Nalaz 7).
    - Koristi unikatni privremeni WIM fajl i zajednički staging lock pri promociji u keš (Nalaz 12).
    """
    def __init__(self):
        self.lock = threading.Lock()
        self.state = {
            "status": "idle",
            "percent": 0.0,
            "current_step": "Spremno",
            "speed_mb_s": 0.0,
            "downloaded_mb": 0,
            "total_mb": 0,
            "eta_seconds": 0,
            "elapsed_seconds": 0,
            "logs": [],
            "message": "",
            "target_build": "",
            "auto_synced_usb": False,
        }
        self.thread = None

    def get_progress(self):
        with self.lock:
            return dict(self.state)

    def log(self, text):
        timestamp = time.strftime("%H:%M:%S")
        entry = f"[{timestamp}] {text}"
        with self.lock:
            self.state["logs"].append(entry)
            if len(self.state["logs"]) > 300:
                self.state["logs"].pop(0)
            self.state["message"] = text

    def start_online_build(self, build_uuid=None, auto_sync_usb_mount=None, edition="professional"):
        with self.lock:
            if self.state["status"] == "running":
                return False, "Automatsko preuzimanje i sklapanje sa interneta je već u toku!"
            self.state = {
                "status": "running",
                "percent": 0.0,
                "current_step": "Inicijalizacija online preuzimanja...",
                "speed_mb_s": 0.0,
                "downloaded_mb": 0,
                "total_mb": 0,
                "eta_seconds": 0,
                "elapsed_seconds": 0,
                "logs": [],
                "message": "Započinjem preuzimanje najnovijeg Windows 11 Builda...",
                "target_build": "",
                "auto_synced_usb": False,
            }

        self.thread = threading.Thread(
            target=self._run_online_build,
            args=(build_uuid, auto_sync_usb_mount, edition),
            daemon=True,
        )
        self.thread.start()
        return True, "Online preuzimanje i sklapanje Windows 11 Builda je pokrenuto u pozadini."

    def _run_online_build(self, build_uuid, auto_sync_usb_mount, edition):
        start_time = time.time()
        attempt_id = f"{int(time.time())}_{os.getpid()}_{uuid.uuid4().hex[:6]}"
        work_dir = None
        loop_dev = None
        tmp_staging_wim = None

        try:
            from .patch_engine import (
                fetch_latest_online_build,
                fetch_uup_download_package,
                get_staging_dir,
            )
            if not build_uuid:
                self.log("Dohvatam najnoviji zvanični Windows 11 Build sa Microsoft kataloga...")
                online_info = fetch_latest_online_build(force_refresh=True)
                build_uuid = online_info.get("uuid") if online_info else None
                target_ver_str = online_info.get("version_str", "Najnoviji Build") if online_info else "Najnoviji Build"
                with self.lock:
                    self.state["target_build"] = target_ver_str
            else:
                target_ver_str = "Zadati Build"

            if not build_uuid:
                raise RuntimeError("Nije moguće pronaći UUID za online preuzimanje. Proverite vezu sa internetom.")

            safe_uuid = re.sub(r"[^a-zA-Z0-9_-]", "", str(build_uuid))
            if not safe_uuid:
                raise RuntimeError(f"Neispravan format build UUID-a: {build_uuid}")

            self.log(f"Ciljani Build za preuzimanje: {target_ver_str} (UUID: {build_uuid}, Attempt: {attempt_id})")

            base_candidates = [
                Path.home() / "Downloads" / "gwadt_uup_build",
                Path.home() / "gwadt_uup_build",
                Path("/tmp/gwadt_uup_build"),
            ]
            storage_p = Path("/home/gru/Storage")
            if storage_p.exists() and os.access(str(storage_p), os.W_OK):
                base_candidates.insert(0, storage_p / "gwadt_uup_build")

            base_dir = None
            for c in base_candidates:
                try:
                    c.mkdir(parents=True, exist_ok=True)
                    base_dir = c
                    break
                except Exception:
                    continue

            if not base_dir:
                base_dir = Path("/tmp/gwadt_uup_build")
                base_dir.mkdir(parents=True, exist_ok=True)

            # Izolovan, garantovano prazan radni direktorijum samo za ovaj pokušaj (Nalaz 7)
            work_dir = base_dir / f"uup_build_{safe_uuid}_{attempt_id}"
            work_dir.mkdir(parents=True, exist_ok=False)

            self.log(f"Izolovani radni direktorijum pokušaja: {work_dir}")

            with self.lock:
                self.state["percent"] = 5.0
                self.state["current_step"] = "Preuzimam konfiguraciju i spisak paketa sa Microsoft servera..."
            self.log("Preuzimam UUP skripte i spisak direktnih Microsoft CDN veza...")

            fetch_uup_download_package(build_uuid, work_dir, edition=edition, lang="en-us")
            self.log("UUP konfiguracioni paket uspešno preuzet i raspakovan.")

            sh_script = work_dir / "uup_download_linux.sh"
            if not sh_script.exists():
                raise RuntimeError("uup_download_linux.sh nije pronađen u raspakovanom paketu!")

            # Auto-patch uup_download_linux.sh i convert.sh za -allow-limited-size
            try:
                sh_content = sh_script.read_text(encoding="utf-8")
                hook = 'aria2c --no-conf --console-log-level=warn --log-level=info --log="aria2_download.log" -x16 -s16 -j2 --allow-overwrite=true --auto-file-renaming=false -d"files" -i"files/converter_multi"'
                patch_str = (
                    hook
                    + "\n"
                    + 'if [ -f files/convert.sh ]; then\n'
                    + '  sed -i \'s/"$genisoimage" -b/"$genisoimage" -allow-limited-size -b/g\' files/convert.sh\n'
                    + '  sed -i \'s/"$genisoimage" --allow-limited-size/"$genisoimage" -allow-limited-size/g\' files/convert.sh\n'
                    + "fi"
                )
                if hook in sh_content and "allow-limited-size" not in sh_content:
                    sh_script.write_text(sh_content.replace(hook, patch_str), encoding="utf-8")

                conv_sh = work_dir / "files" / "convert.sh"
                if conv_sh.exists():
                    c_txt = conv_sh.read_text(encoding="utf-8")
                    if "-allow-limited-size" not in c_txt:
                        c_txt = c_txt.replace('"$genisoimage" -b', '"$genisoimage" -allow-limited-size -b')
                        conv_sh.write_text(c_txt, encoding="utf-8")
            except Exception as pe:
                self.log(f"Patch upozorenje: {pe}")

            with self.lock:
                self.state["percent"] = 15.0
                self.state["current_step"] = "Pokrećem višenitno preuzimanje sa Microsoft CDN-a (aria2c)..."
            self.log("Pokrećem uup_download_linux.sh proces...")

            env = os.environ.copy()
            env["LANG"] = "C.UTF-8"

            proc = subprocess.Popen(
                ["/bin/bash", str(sh_script)],
                cwd=str(work_dir),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
                env=env,
            )

            aria_re = re.compile(
                r"\[#\w+\s+([\d\.]+[KMG]i?B)/([\d\.]+[KMG]i?B)\((\d+)%\)\s+CN:\d+\s+DL:([\d\.]+[KMG]i?B)\]"
            )

            for line in iter(proc.stdout.readline, ""):
                line_str = line.strip()
                if not line_str:
                    continue

                m = aria_re.search(line_str)
                if m:
                    dl_pct = int(m.group(3))
                    speed_str = m.group(4)
                    speed_num = 0.0
                    try:
                        if "MiB" in speed_str or "MB" in speed_str:
                            speed_num = float(re.findall(r"[\d\.]+", speed_str)[0])
                        elif "KiB" in speed_str or "KB" in speed_str:
                            speed_num = float(re.findall(r"[\d\.]+", speed_str)[0]) / 1024.0
                    except Exception:
                        pass

                    mapped_pct = 15.0 + (dl_pct * 0.60)
                    with self.lock:
                        self.state["percent"] = round(mapped_pct, 1)
                        self.state["speed_mb_s"] = round(speed_num, 1)
                        self.state["current_step"] = f"Preuzimanje sa Microsoft CDN-a: {dl_pct}% ({speed_str}/s)"
                        self.state["elapsed_seconds"] = int(time.time() - start_time)
                elif (
                    "Converting" in line_str
                    or "Creating" in line_str
                    or "convert" in line_str.lower()
                    or "wimlib" in line_str.lower()
                ):
                    with self.lock:
                        self.state["percent"] = 80.0
                        self.state["current_step"] = "Sklapanje i sabijanje Windows 11 WIM slike..."
                    self.log(line_str)
                else:
                    if len(line_str) < 120 and ("aria2c" not in line_str):
                        self.log(line_str)

            proc.stdout.close()
            ret_code = proc.wait()

            # Stroga provera izlaznog koda bez prihvatanja neuspešnog builda (Nalaz 7)
            if ret_code != 0:
                raise RuntimeError(f"uup_download_linux.sh nije uspeo (exit code {ret_code})!")

            generated_wims = (
                list((work_dir / "ISODIR" / "sources").glob("install.wim"))
                + list(work_dir.glob("*.wim"))
                + list((work_dir / "UUPs").glob("install.wim"))
            )
            generated_isos = list(work_dir.glob("*.iso")) + list(work_dir.glob("*.ISO"))

            with self.lock:
                self.state["percent"] = 88.0
                self.state["current_step"] = "Verifikacija sklopljene slike iz izolovanog pokušaja..."
            self.log("Preuzimanje i sklapanje je završeno (exit 0). Verifikujem generisani WIM/ISO u radnom direktorijumu...")

            final_source_wim = None
            work_dir_real = work_dir.resolve()
            if generated_wims:
                cand_wim = generated_wims[0].resolve()
                if not str(cand_wim).startswith(str(work_dir_real) + os.sep):
                    raise RuntimeError(f"Pronađeni WIM ne pripada izolovanom direktorijumu pokušaja: {cand_wim}")
                final_source_wim = cand_wim
                self.log(f"Pronađen direktan WIM fajl pokušaja: {final_source_wim.name}")
            elif generated_isos:
                iso_file = generated_isos[0].resolve()
                if not str(iso_file).startswith(str(work_dir_real) + os.sep):
                    raise RuntimeError(f"Pronađeni ISO ne pripada izolovanom direktorijumu pokušaja: {iso_file}")
                self.log(f"Pronađen generisani ISO fajl pokušaja: {iso_file.name}. Montiram radi izvlačenja install.wim...")
                mount_dir, loop_dev = GLOBAL_PROGRAM_REFRESH_WORKER._mount_iso(iso_file)
                candidates_src = [
                    mount_dir / "sources" / "install.wim",
                    mount_dir / "sources" / "install.esd",
                ]
                final_source_wim = next((s for s in candidates_src if s.exists()), None)
                if not final_source_wim:
                    raise RuntimeError("U generisanom ISO fajlu nema sources/install.wim!")

            if not final_source_wim or not final_source_wim.exists():
                raise RuntimeError("Nije pronađen ni WIM ni ISO u izolovanom radnom direktorijumu nakon završetka skripte!")

            from .system_inspector import read_wim_metadata
            source_editions = read_wim_metadata(str(final_source_wim))
            if not source_editions:
                raise RuntimeError("Generisana slika iz online builda nema čitljive WIM metapodatke!")

            with self.lock:
                self.state["percent"] = 92.0
                self.state["current_step"] = "Optimizacija i sabijanje 'Windows 11 Pro' u programski keš..."
            self.log("Sabijam i optimizujem 'Windows 11 Pro' ediciju u Pre-Stage keš...")

            staging_dir = get_staging_dir()
            staging_dir.mkdir(parents=True, exist_ok=True)
            target_staging_wim = staging_dir / "install.wim"
            tmp_staging_wim = staging_dir / f"install_building_online_{attempt_id}.wim"

            export_cmd = [
                "wimlib-imagex",
                "export",
                str(final_source_wim),
                "Windows 11 Pro",
                str(tmp_staging_wim),
                "--compress=maximum",
            ]
            exp_res = subprocess.run(export_cmd, capture_output=True, text=True)
            if exp_res.returncode != 0:
                self.log("Pokušavam izvoz po indeksu 1...")
                export_cmd[3] = "1"
                exp_res = subprocess.run(export_cmd, capture_output=True, text=True)
                if exp_res.returncode != 0:
                    raise RuntimeError(f"wimlib-imagex export greška (exit {exp_res.returncode}): {exp_res.stderr}")

            if not tmp_staging_wim.exists() or tmp_staging_wim.stat().st_size == 0:
                raise RuntimeError("Izvezeni privremeni staging WIM ne postoji ili je prazan!")

            editions = read_wim_metadata(str(tmp_staging_wim))
            if not editions:
                raise RuntimeError("Izvezeni privremeni staging WIM nema validne WIM metapodatke!")

            pro_ed = editions[0]
            size_bytes = tmp_staging_wim.stat().st_size
            size_mb = size_bytes // (1024 * 1024)
            wim_sha256 = calc_sha256(tmp_staging_wim)

            b_num = pro_ed.get("build") or 26200
            sp_num = pro_ed.get("spbuild") or 8037
            new_meta = {
                "edition": pro_ed.get("name", "Windows 11 Pro"),
                "build": b_num,
                "spbuild": sp_num,
                "version_str": pro_ed.get("version_str", f"10.0.{b_num}.{sp_num}"),
                "size_bytes": size_bytes,
                "size_mb": size_mb,
                "sha256": wim_sha256,
                "build_uuid": build_uuid,
                "attempt_id": attempt_id,
                "prepared_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            }

            with acquire_staging_lock(staging_dir):
                os.replace(tmp_staging_wim, target_staging_wim)
                tmp_staging_wim = None
                meta_file = staging_dir / "staging_meta.json"
                tmp_meta = staging_dir / f".staging_meta.json.tmp.{attempt_id}"
                with open(tmp_meta, "w", encoding="utf-8") as mf:
                    json.dump(new_meta, mf, indent=2, ensure_ascii=False)
                    mf.flush()
                    os.fsync(mf.fileno())
                os.replace(tmp_meta, meta_file)

            self.log(f"Nova slika uspešno sačuvana u kešu: Build {new_meta['version_str']} ({size_mb} MB, SHA-256: {wim_sha256[:12]})!")

            auto_synced = False
            if auto_sync_usb_mount and os.path.exists(auto_sync_usb_mount):
                with self.lock:
                    self.state["percent"] = 96.0
                    self.state["current_step"] = "Automatski prenos novog Builda na USB fleš..."
                self.log(f"Automatski prenosim novi Build {new_meta['version_str']} na USB: {auto_sync_usb_mount}...")
                ok, msg = GLOBAL_UPGRADE_WORKER.start_upgrade(
                    auto_sync_usb_mount,
                    source_wim_path=target_staging_wim,
                    force=True,
                )
                if not ok:
                    raise RuntimeError(f"Automatski prenos na USB nije mogao biti pokrenut: {msg}")
                while True:
                    time.sleep(1)
                    up_prog = GLOBAL_UPGRADE_WORKER.get_progress()
                    if up_prog["status"] in ["success", "error"]:
                        break
                if up_prog["status"] != "success":
                    raise RuntimeError(f"Automatski prenos na USB nije uspeo: {up_prog.get('message', '')}")
                auto_synced = True

            elapsed_total = int(time.time() - start_time)
            with self.lock:
                self.state["status"] = "success"
                self.state["percent"] = 100.0
                self.state["elapsed_seconds"] = elapsed_total
                self.state["current_step"] = f"Završeno! Najnoviji Build {new_meta['version_str']} je u kešu!"
                self.state["message"] = f"Uspešno preuzet i pripremljen Build {new_meta['version_str']}!"
                self.state["auto_synced_usb"] = auto_synced

            self.log(f"ČESTITAMO: Najnoviji Windows 11 Build {new_meta['version_str']} je uspešno preuzet i pripremljen za {elapsed_total}s!")

        except Exception as e:
            err = str(e)
            self.log(f"GRESKA tokom online preuzimanja i sklapanja: {err}")
            if tmp_staging_wim is not None and tmp_staging_wim.exists():
                try:
                    tmp_staging_wim.unlink()
                except Exception:
                    pass
            with self.lock:
                self.state["status"] = "error"
                self.state["current_step"] = "Greška tokom operacije"
                self.state["message"] = err
        finally:
            if loop_dev:
                GLOBAL_PROGRAM_REFRESH_WORKER._unmount_iso(loop_dev)


GLOBAL_ONLINE_BUILD_WORKER = OnlineBuildWorker()
