# -*- coding: utf-8 -*-
"""
usb_detector.py - Detekcija, inspekcija i stroga server-side validacija USB skladišnih uređaja
Podržava Linux (lsblk, sysfs, udevadm, udisksctl, /run/media) i Windows (PowerShell/CIM).
Sadrži zaštitne mehanizme protiv pristupa sistemskim/mrežnim diskovima, symlink preusmeravanja
i konkurentnih mutirajućih operacija nad istim uređajem.
"""

import os
import re
import sys
import json
import time
import shutil
import platform
import threading
import subprocess
from contextlib import contextmanager
from pathlib import Path

# ==============================================================================
# 1. BEZBEDNOSNE KONSTANTE I BLOKADA SISTEMSKIH / MREŽNIH DISKOVA
# ==============================================================================

# Putanje koje su apsolutno zabranjene kao ciljevi u svim režimima (uključujući i --dev)
FORBIDDEN_EXACT_PATHS = {
    "/",
    "/boot",
    "/boot/efi",
    "/home",
    "/usr",
    "/var",
    "/etc",
    "/tmp",
    "/opt",
    "/srv",
    "/root",
    "/proc",
    "/sys",
    "/dev",
    "/run",
    "/mnt",
    "/media",
    "/run/media",
    "/mnt/TK_NAS",
}

# Prefiksi putanja koji su uvek zabranjeni (sve podputanje unutar njih su blokirane)
FORBIDDEN_PATH_PREFIXES = (
    "/boot/",
    "/usr/",
    "/var/",
    "/etc/",
    "/tmp/",
    "/proc/",
    "/sys/",
    "/root/",
    "/mnt/TK_NAS/",
    "/dev/mapper/",
    "/dev/dm-",
    "/dev/loop",
    "/dev/zram",
    "/dev/ram",
    "/dev/md",
)

# Sistemske tačke montiranja koje identifikuju sistemski disk hosta
SYSTEM_CRITICAL_MOUNTPOINTS = {
    "/",
    "/boot",
    "/boot/efi",
    "/home",
    "/usr",
    "/var",
    "/etc",
    "/tmp",
    "/opt",
    "/srv",
    "[SWAP]",
}

# Mrežni tipovi fajlsistema koji nikada ne smeju biti tretirani kao USB ciljevi
NETWORK_FS_TYPES = {
    "cifs",
    "smb3",
    "smbfs",
    "nfs",
    "nfs4",
    "fuse.sshfs",
    "fuse.rclone",
    "9p",
    "ceph",
    "glusterfs",
    "davfs",
    "afs",
}

# Dozvoljene ne-destruktivne operacije nad lokalnim razvojnim repozitorijumom (--dev)
DEV_REPO_ALLOWED_OPERATIONS = {"sync", "sync_scripts", "inspect", "read_log"}

# Destruktivne ili hardverske operacije koje su STROGO ZABRANJENE nad --dev repozitorijumom
DEV_REPO_FORBIDDEN_OPERATIONS = {
    "eject",
    "clear_dirty",
    "format",
    "wipe",
    "partition",
    "upgrade",
    "upgrade_usb",
}

# ==============================================================================
# 2. KONTROLA KONKURENTNOSTI (PER-TARGET BUSY LOCK)
# ==============================================================================

_ACTIVE_TARGET_LOCKS = {}
_ACTIVE_TARGET_LOCKS_GUARD = threading.Lock()


def normalize_target_lock_key(target_info_or_str):
    """
    Određuje kanonski ključ za zaključavanje uređaja tako da sve particije
    istog fizičkog USB diska (ili njegova tačka montiranja) dele isti lock.
    """
    if isinstance(target_info_or_str, dict):
        if target_info_or_str.get("is_dev_repo"):
            return "LOCK::PROJEKAT_REPOZITORIJUM"
        parent = target_info_or_str.get("parent_device") or ""
        dev = target_info_or_str.get("canonical_device") or target_info_or_str.get("device") or ""
        mp = target_info_or_str.get("canonical_mountpoint") or target_info_or_str.get("mountpoint") or ""
        key_base = parent or dev or mp or "UNKNOWN"
        return f"LOCK::{key_base}"
    raw = str(target_info_or_str or "").strip()
    if not raw:
        return "LOCK::UNKNOWN"
    if raw == "PROJEKAT_REPOZITORIJUM":
        return "LOCK::PROJEKAT_REPOZITORIJUM"
    return f"LOCK::{raw}"


def _cleanup_stale_lock_unlocked(lock_key):
    """Uklanja zastareli lock ako je vezana pozadinska nit završila izvršavanje."""
    entry = _ACTIVE_TARGET_LOCKS.get(lock_key)
    if not entry:
        return
    worker_thread = entry.get("worker_thread")
    if worker_thread is not None and not worker_thread.is_alive():
        _ACTIVE_TARGET_LOCKS.pop(lock_key, None)


def acquire_target_lock(target_key, operation="mutation", worker_thread=None):
    """
    Atomski pokušava da zaključa ciljni uređaj za datu mutirajuću operaciju.
    Vraća (True, lock_entry) ako je zaključavanje uspelo, ili (False, existing_entry) ako je zauzeto.
    """
    lock_key = normalize_target_lock_key(target_key)
    with _ACTIVE_TARGET_LOCKS_GUARD:
        _cleanup_stale_lock_unlocked(lock_key)
        if lock_key in _ACTIVE_TARGET_LOCKS:
            return False, dict(_ACTIVE_TARGET_LOCKS[lock_key])
        entry = {
            "lock_key": lock_key,
            "operation": operation,
            "started_at": time.time(),
            "thread_id": threading.get_ident(),
            "worker_thread": worker_thread,
        }
        _ACTIVE_TARGET_LOCKS[lock_key] = entry
        return True, dict(entry)


def attach_worker_thread_to_lock(target_key, worker_thread):
    """Povezuje referencu na pozadinsku nit sa aktivnim lock-om."""
    lock_key = normalize_target_lock_key(target_key)
    with _ACTIVE_TARGET_LOCKS_GUARD:
        if lock_key in _ACTIVE_TARGET_LOCKS:
            _ACTIVE_TARGET_LOCKS[lock_key]["worker_thread"] = worker_thread


def release_target_lock(target_key):
    """Oslobađa zaključani ciljni uređaj."""
    lock_key = normalize_target_lock_key(target_key)
    with _ACTIVE_TARGET_LOCKS_GUARD:
        _ACTIVE_TARGET_LOCKS.pop(lock_key, None)


def is_target_locked(target_key):
    """Proverava da li je ciljni uređaj trenutno zaključan aktivnom operacijom."""
    lock_key = normalize_target_lock_key(target_key)
    with _ACTIVE_TARGET_LOCKS_GUARD:
        _cleanup_stale_lock_unlocked(lock_key)
        return lock_key in _ACTIVE_TARGET_LOCKS


@contextmanager
def target_operation_lock(target_key, operation="mutation"):
    """Kontekst menadžer za per-target zaključavanje sinhronih operacija."""
    ok, info = acquire_target_lock(target_key, operation=operation)
    if not ok:
        raise RuntimeError(
            f"Uređaj je trenutno zauzet aktivnom operacijom ({info.get('operation', 'u toku')})."
        )
    try:
        yield info
    finally:
        release_target_lock(target_key)


# ==============================================================================
# 3. POMOĆNE FUNKCIJE ZA INSPEKCIJU HARDVERA, SYMLINKOVA I SISTEMSKIH DISKOVA
# ==============================================================================

def _format_size(bytes_size):
    """Formatira bajtove u čitljiv format (GB, MB)."""
    if bytes_size is None or bytes_size < 0:
        return "N/A"
    gb = bytes_size / (1024 ** 3)
    if gb >= 1.0:
        return f"{gb:.1f} GB"
    mb = bytes_size / (1024 ** 2)
    return f"{mb:.0f} MB"


def _get_project_root():
    """Vraća kanonsku putanju korena projekta."""
    return Path(
        os.environ.get("GWADT_PROJECT_ROOT", str(Path(__file__).resolve().parent.parent.parent))
    ).resolve()


def _has_symlink_in_path(path_str):
    """
    Proverava da li zadata putanja ili bilo koja njena roditeljska komponenta
    sadrži simbolički link (symlink preusmeravanje).
    Vraća (True, razlog) ako je symlink detektovan, ili (False, "") ako je putanja čista.
    """
    if not path_str:
        return False, ""
    raw = str(path_str).strip()
    if raw == "PROJEKAT_REPOZITORIJUM":
        return False, ""

    try:
        # Provera na Linux/POSIX putanjama ili postojećim putanjama
        if raw.startswith("/") or os.path.exists(raw) or os.path.lexists(raw):
            stripped = raw.rstrip("/") or "/"
            if os.path.islink(stripped):
                return True, f"Putanja '{raw}' je simbolički link."

            p = Path(raw)
            curr = Path("/") if raw.startswith("/") else Path(".")
            for part in p.parts:
                if part in ("/", "\\", ""):
                    continue
                curr = curr / part
                if os.path.islink(str(curr)):
                    return True, f"Segment putanje '{curr}' je simbolički link."

            norm_abs = os.path.abspath(os.path.normpath(raw))
            real_p = os.path.realpath(raw)
            if real_p != norm_abs:
                return True, f"Kanonska putanja '{real_p}' se razlikuje od '{norm_abs}' (detektovano symlink preusmeravanje)."
    except Exception as e:
        return True, f"Greška pri proveri symlinkova za '{raw}': {e}"

    return False, ""


def _resolve_parent_block_name(dev_name):
    """
    Za zadato ime blok uređaja (npr. 'sda1' ili 'nvme0n1p2') pronalazi ime
    roditeljskog fizičkog diska (npr. 'sda' ili 'nvme0n1') preko /sys/class/block.
    """
    clean = os.path.basename(str(dev_name or "").strip())
    if not clean:
        return ""
    sys_block = f"/sys/class/block/{clean}"
    if os.path.exists(sys_block):
        try:
            real_sys = os.path.realpath(sys_block)
            parent_dir = os.path.basename(os.path.dirname(real_sys))
            if parent_dir and parent_dir != "block" and os.path.exists(f"/sys/block/{parent_dir}"):
                return parent_dir
        except Exception:
            pass
    # Fallback heuristika za standardna Linux imena particija
    m_nvme = re.match(r"^(nvme\d+n\d+|mmcblk\d+)p\d+$", clean)
    if m_nvme:
        return m_nvme.group(1)
    m_sd = re.match(r"^([a-z]+)\d+$", clean)
    if m_sd:
        return m_sd.group(1)
    return clean


def _get_system_and_network_guard_data():
    """
    Očitava sve sistemske blok uređaje (uključujući cele diskove na kojima se nalaze
    root, boot, efi, home, usr, var, tmp ili swap particije) i sve mrežne tačke montiranja.
    """
    system_devices = set()
    system_disk_names = set()
    network_mounts = {"/mnt/TK_NAS"}

    if platform.system() == "Windows":
        sys_drive = os.environ.get("SystemDrive", "C:").upper().rstrip("\\")
        system_devices.add(sys_drive)
        system_devices.add(sys_drive + "\\")
        return system_devices, network_mounts

    # 1. Čitanje /proc/mounts za mrežne fajlsisteme i sistemske tačke montiranja
    try:
        if os.path.exists("/proc/mounts"):
            with open("/proc/mounts", "r", encoding="utf-8", errors="ignore") as f:
                for line in f:
                    parts = line.split()
                    if len(parts) < 3:
                        continue
                    src, mnt, fstype = parts[0], parts[1], parts[2].lower()
                    # Dekodiranje oktalnih escape sekvenci u /proc/mounts (npr. \040 za razmak)
                    mnt = mnt.replace("\\040", " ")

                    if (
                        fstype in NETWORK_FS_TYPES
                        or src.startswith("//")
                        or src.startswith("\\\\")
                        or (":" in src and not src.startswith("/dev/"))
                        or mnt == "/mnt/TK_NAS"
                        or mnt.startswith("/mnt/TK_NAS/")
                    ):
                        network_mounts.add(mnt)

                    if (
                        mnt in SYSTEM_CRITICAL_MOUNTPOINTS
                        or mnt.startswith("/boot/")
                        or mnt.startswith("/usr/")
                        or mnt.startswith("/var/")
                    ):
                        if src.startswith("/dev/"):
                            system_devices.add(src)
                            real_src = os.path.realpath(src)
                            system_devices.add(real_src)
                            bname = os.path.basename(real_src)
                            p_disk = _resolve_parent_block_name(bname)
                            if p_disk:
                                system_disk_names.add(p_disk)
                                system_devices.add(f"/dev/{p_disk}")
    except Exception:
        pass

    # 2. Čitanje lsblk stabla za kompletnu mapu particija sistemskih diskova
    try:
        cmd = ["lsblk", "-J", "-o", "NAME,PKNAME,MOUNTPOINTS,TYPE,FSTYPE"]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=4)
        if proc.returncode == 0 and proc.stdout.strip():
            data = json.loads(proc.stdout)

            def _scan_node(node, top_disk_name=None):
                name = node.get("name") or ""
                pkname = node.get("pkname") or top_disk_name or ""
                root_disk = top_disk_name or name
                mounts = [m for m in (node.get("mountpoints") or []) if m]
                is_sys = False
                for m in mounts:
                    if (
                        m in SYSTEM_CRITICAL_MOUNTPOINTS
                        or m.startswith("/boot/")
                        or m.startswith("/usr/")
                        or m.startswith("/var/")
                        or m == "/mnt/TK_NAS"
                        or m.startswith("/mnt/TK_NAS/")
                    ):
                        is_sys = True
                        break
                for child in node.get("children") or []:
                    if _scan_node(child, top_disk_name=root_disk):
                        is_sys = True
                if is_sys:
                    if name:
                        system_devices.add(f"/dev/{name}")
                    if pkname:
                        system_disk_names.add(pkname)
                        system_devices.add(f"/dev/{pkname}")
                    if root_disk:
                        system_disk_names.add(root_disk)
                        system_devices.add(f"/dev/{root_disk}")
                return is_sys

            for dev in data.get("blockdevices") or []:
                _scan_node(dev, top_disk_name=dev.get("name"))

            # Drugi prolaz: svaka particija čiji je roditelj u system_disk_names je takođe zabranjena
            def _mark_children(node, top_disk_name=None):
                name = node.get("name") or ""
                pkname = node.get("pkname") or top_disk_name or ""
                if name in system_disk_names or pkname in system_disk_names or top_disk_name in system_disk_names:
                    if name:
                        system_devices.add(f"/dev/{name}")
                for child in node.get("children") or []:
                    _mark_children(child, top_disk_name=top_disk_name or name)

            for dev in data.get("blockdevices") or []:
                _mark_children(dev, top_disk_name=dev.get("name"))
    except Exception:
        pass

    # Dodaj sve particije sistemskih diskova iz /sys/block/<disk>/
    for disk_name in list(system_disk_names):
        sys_disk_dir = Path(f"/sys/block/{disk_name}")
        if sys_disk_dir.is_dir():
            try:
                for sub in sys_disk_dir.iterdir():
                    if sub.name.startswith(disk_name):
                        system_devices.add(f"/dev/{sub.name}")
            except Exception:
                pass

    return system_devices, network_mounts


def _query_udev_properties(dev_node):
    """Očitava udev svojstva za dati blok uređaj (ID_BUS, ID_SERIAL_SHORT, ID_FS_UUID, ID_MODEL)."""
    props = {}
    if not dev_node or platform.system() == "Windows":
        return props
    try:
        proc = subprocess.run(
            ["udevadm", "info", "--query=property", f"--name={dev_node}"],
            capture_output=True,
            text=True,
            timeout=2,
        )
        if proc.returncode == 0:
            for line in proc.stdout.splitlines():
                if "=" in line:
                    k, v = line.split("=", 1)
                    props[k.strip()] = v.strip()
    except Exception:
        pass
    return props


def _verify_linux_usb_hardware(dev_name, parent_name=None, tran="", rm=False, model="", serial="", uuid="", system_devices=None):
    """
    Proverava da li fizički Linux blok uređaj zaista pripada USB magistrali
    i da nije sistemski disk.
    Podržava i eksterne USB SSD diskove sa rm=0 isključivo ako im je transport
    dokazano 'usb' (preko lsblk tran, /sys/class/block/.../usb ili udev ID_BUS=usb),
    nisu sistemski disk i imaju hardverski identitet (serial, uuid ili model).
    Vraća (is_valid_usb: bool, details: dict, reason: str).
    """
    clean_dev = os.path.basename(str(dev_name or "").strip())
    clean_parent = os.path.basename(str(parent_name or "").strip()) or _resolve_parent_block_name(clean_dev)

    if not clean_dev:
        return False, {}, "Prazno ime blok uređaja."

    if any(clean_dev.startswith(pfx) for pfx in ("loop", "ram", "zram", "dm-", "md", "sr")):
        return False, {}, f"Uređaj '/dev/{clean_dev}' je virtuelni/loop/optički uređaj."

    if system_devices is None:
        system_devices, _ = _get_system_and_network_guard_data()

    dev_node = f"/dev/{clean_dev}"
    parent_node = f"/dev/{clean_parent}" if clean_parent else dev_node

    if dev_node in system_devices or parent_node in system_devices:
        return False, {}, f"Uređaj '{dev_node}' pripada sistemskom disku hosta!"

    # 1. Provera lsblk transporta
    tran_norm = (tran or "").strip().lower()
    is_tran_usb = tran_norm == "usb"

    # 2. Provera sysfs putanje (/sys/class/block/<dev> ili /sys/block/<parent>/device)
    is_sysfs_usb = False
    for candidate in (clean_dev, clean_parent):
        if not candidate:
            continue
        sys_p = f"/sys/class/block/{candidate}"
        if os.path.exists(sys_p):
            try:
                real_sys = os.path.realpath(sys_p).lower()
                if "/usb" in real_sys:
                    is_sysfs_usb = True
                    break
            except Exception:
                pass
        sys_dev_p = f"/sys/block/{candidate}/device"
        if os.path.exists(sys_dev_p):
            try:
                real_dev = os.path.realpath(sys_dev_p).lower()
                if "/usb" in real_dev:
                    is_sysfs_usb = True
                    break
            except Exception:
                pass

    # 3. Provera udev svojstava (ID_BUS=usb i fallback za serial/uuid/model)
    udev_props = _query_udev_properties(dev_node)
    if not udev_props and parent_node != dev_node:
        udev_props = _query_udev_properties(parent_node)

    is_udev_usb = udev_props.get("ID_BUS", "").lower() == "usb"
    resolved_serial = (
        serial
        or udev_props.get("ID_SERIAL_SHORT")
        or udev_props.get("ID_SERIAL")
        or ""
    ).strip()
    resolved_uuid = (uuid or udev_props.get("ID_FS_UUID") or "").strip()
    resolved_model = (
        model
        or udev_props.get("ID_MODEL_ENC", "").replace("\\x20", " ")
        or udev_props.get("ID_MODEL")
        or ""
    ).strip()

    is_usb_bus = bool(is_tran_usb or is_sysfs_usb or is_udev_usb)
    is_removable = bool(rm)

    # Ako transport eksplicitno ukazuje na internu magistralu (nvme, sata, ata, pcie, virtio, vmd)
    # a nema nikakvog dokaza o USB mostu, odbijamo!
    if tran_norm in {"nvme", "sata", "ata", "pcie", "virtio", "sas", "mmc"} and not is_sysfs_usb and not is_udev_usb:
        return False, {}, f"Interni disk transport ('{tran_norm}') nije dozvoljen kao USB cilj."

    # Pravilo za eksterne USB SSD uređaje (rm == 0):
    # Moraju imati dokazani USB transport (is_usb_bus == True) i poznat identitet (serial/uuid/model).
    if not is_removable:
        if not is_usb_bus:
            return False, {}, f"Uređaj '{dev_node}' nije removable (rm=0) i nema dokazan USB transport."
        if not (resolved_serial or resolved_uuid or resolved_model):
            return False, {}, f"Eksterni USB uređaj '{dev_node}' (rm=0) nema proverljiv identitet (serial/uuid/model)."

    # Za removable uređaje (rm == 1), zahtevamo ili dokazan USB transport ili da nisu na internoj magistrali
    if is_removable and not is_usb_bus and tran_norm not in ("", "usb"):
        return False, {}, f"Removable uređaj '{dev_node}' koristi nedozvoljen transport '{tran_norm}'."

    details = {
        "is_usb_bus": is_usb_bus,
        "tran": "usb" if is_usb_bus else tran_norm,
        "rm": is_removable,
        "serial": resolved_serial,
        "uuid": resolved_uuid,
        "model": resolved_model or "USB Storage Device",
    }
    return True, details, "OK"


def _is_forbidden_path(path_str, system_devices=None, network_mounts=None):
    """
    Proverava da li je zadata putanja na listi strogo zabranjenih sistemskih ili mrežnih lokacija.
    Ova provera važi apsolutno uvek, čak i kada je uključen --dev režim!
    """
    if not path_str:
        return True, "Prazna putanja nije dozvoljena."

    raw = str(path_str).strip()
    if raw == "PROJEKAT_REPOZITORIJUM":
        return False, ""

    # Windows sistemske i UNC putanje
    if platform.system() == "Windows":
        norm_win = raw.replace("/", "\\").rstrip("\\").upper()
        sys_drive = os.environ.get("SystemDrive", "C:").upper().rstrip("\\")
        if norm_win in {sys_drive, f"{sys_drive}\\WINDOWS", f"{sys_drive}\\USERS", f"{sys_drive}\\PROGRAM FILES"}:
            return True, f"Pristup Windows sistemskoj putanji '{raw}' je strogo zabranjen!"
        if raw.startswith("\\\\"):
            return True, f"Mrežne UNC putanje ('{raw}') su strogo zabranjene!"
        return False, ""

    # POSIX normalizacija
    norm_p = os.path.abspath(os.path.normpath(raw))
    real_p = os.path.realpath(raw) if (os.path.exists(raw) or os.path.lexists(raw)) else norm_p

    home_dir = str(Path.home())
    forbidden_exact = set(FORBIDDEN_EXACT_PATHS) | {home_dir}

    for candidate in (norm_p, real_p):
        if candidate in forbidden_exact:
            return True, f"Sistemska ili mrežna putanja '{candidate}' je strogo zabranjena!"
        for pfx in FORBIDDEN_PATH_PREFIXES:
            if candidate.startswith(pfx):
                return True, f"Putanja '{candidate}' unutar zaštićenog sistemskog/mrežnog stabla ('{pfx}') je strogo zabranjena!"

    if system_devices is None or network_mounts is None:
        system_devices, network_mounts = _get_system_and_network_guard_data()

    for candidate in (norm_p, real_p):
        if candidate in system_devices:
            return True, f"Blok uređaj '{candidate}' pripada sistemskom disku hosta (root/boot/home) i strogo je zabranjen!"
        for net_mnt in network_mounts:
            if candidate == net_mnt or candidate.startswith(net_mnt.rstrip("/") + "/"):
                return True, f"Mrežna lokacija '{candidate}' ('{net_mnt}') je strogo zabranjena!"

    return False, ""


# ==============================================================================
# 4. DETEKCIJA USB UREĐAJA (LINUX I WINDOWS)
# ==============================================================================

def _build_device_id(dev_node, uuid="", serial="", label=""):
    """Generiše deterministički server-side ID uređaja."""
    clean_dev = os.path.basename(str(dev_node or "").strip().rstrip("\\/"))
    ident = (uuid or serial or label or "usb").strip()
    ident_safe = re.sub(r"[^A-Za-z0-9_.-]", "_", ident)
    return f"usb::{clean_dev}::{ident_safe}"


def detect_linux_usb_drives():
    """
    Očitava sve verifikovane USB prenosne uređaje na Linux operativnom sistemu
    koristeći 'lsblk -J', sysfs/udev verifikaciju magistrale i zaštitu sistemskih diskova.
    """
    drives = []
    system_devices, network_mounts = _get_system_and_network_guard_data()

    try:
        cmd = [
            "lsblk", "-J", "-b", "-o",
            "NAME,PKNAME,RM,RO,TYPE,SIZE,MOUNTPOINTS,LABEL,MODEL,SERIAL,UUID,TRAN,FSTYPE"
        ]
        proc = subprocess.run(cmd, capture_output=True, text=True, timeout=5)
        if proc.returncode == 0 and proc.stdout.strip():
            data = json.loads(proc.stdout)
            blockdevices = data.get("blockdevices", [])

            for dev in blockdevices:
                dev_name = dev.get("name", "")
                dev_type = dev.get("type", "")
                if dev_type in ["loop", "rom", "ram", "zram", "lvm", "crypt"]:
                    continue

                dev_node = f"/dev/{dev_name}"
                if dev_node in system_devices:
                    continue

                dev_tran = dev.get("tran") or ""
                dev_rm = bool(dev.get("rm"))
                dev_model = (dev.get("model") or "").strip()
                dev_serial = (dev.get("serial") or "").strip()
                dev_uuid = (dev.get("uuid") or "").strip()
                size_bytes = int(dev.get("size") or 0)

                ok_usb, hw_info, _ = _verify_linux_usb_hardware(
                    dev_name=dev_name,
                    parent_name=dev_name,
                    tran=dev_tran,
                    rm=dev_rm,
                    model=dev_model,
                    serial=dev_serial,
                    uuid=dev_uuid,
                    system_devices=system_devices,
                )
                if not ok_usb:
                    continue

                model = hw_info["model"]
                serial = hw_info["serial"]
                children = dev.get("children", [])

                if children:
                    for child in children:
                        c_name = child.get("name", "")
                        if not c_name:
                            continue
                        c_node = f"/dev/{c_name}"
                        if c_node in system_devices:
                            continue

                        c_mounts = [m for m in (child.get("mountpoints") or []) if m]
                        c_label = (child.get("label") or "").strip()
                        c_fstype = (child.get("fstype") or "").strip()
                        c_uuid = (child.get("uuid") or dev_uuid or "").strip()
                        c_size = int(child.get("size") or 0)

                        mount_point = c_mounts[0] if c_mounts else None
                        if mount_point:
                            forb, _ = _is_forbidden_path(mount_point, system_devices, network_mounts)
                            has_sym, _ = _has_symlink_in_path(mount_point)
                            if forb or has_sym:
                                continue

                        free_bytes = None
                        used_percent = 0.0
                        if mount_point and os.path.exists(mount_point):
                            try:
                                du = shutil.disk_usage(mount_point)
                                free_bytes = du.free
                                if du.total > 0:
                                    used_percent = round(((du.total - du.free) / du.total) * 100, 1)
                            except Exception:
                                pass

                        drives.append({
                            "device_id": _build_device_id(c_node, uuid=c_uuid, serial=serial, label=c_label),
                            "device": c_node,
                            "parent_device": dev_node,
                            "model": model,
                            "serial": serial,
                            "uuid": c_uuid,
                            "tran": hw_info["tran"],
                            "rm": hw_info["rm"],
                            "label": c_label or "Bez Naziva",
                            "fstype": c_fstype,
                            "mountpoint": mount_point,
                            "is_mounted": mount_point is not None,
                            "size_bytes": c_size,
                            "size_human": _format_size(c_size),
                            "free_bytes": free_bytes,
                            "free_human": _format_size(free_bytes),
                            "used_percent": used_percent,
                            "is_usb": True,
                            "is_dev_repo": False,
                        })
                else:
                    mounts = [m for m in (dev.get("mountpoints") or []) if m]
                    mount_point = mounts[0] if mounts else None
                    if mount_point:
                        forb, _ = _is_forbidden_path(mount_point, system_devices, network_mounts)
                        has_sym, _ = _has_symlink_in_path(mount_point)
                        if forb or has_sym:
                            continue

                    free_bytes = None
                    used_percent = 0.0
                    if mount_point and os.path.exists(mount_point):
                        try:
                            du = shutil.disk_usage(mount_point)
                            free_bytes = du.free
                            if du.total > 0:
                                used_percent = round(((du.total - du.free) / du.total) * 100, 1)
                        except Exception:
                            pass

                    d_label = (dev.get("label") or "USB Disk").strip()
                    drives.append({
                        "device_id": _build_device_id(dev_node, uuid=dev_uuid, serial=serial, label=d_label),
                        "device": dev_node,
                        "parent_device": dev_node,
                        "model": model,
                        "serial": serial,
                        "uuid": dev_uuid,
                        "tran": hw_info["tran"],
                        "rm": hw_info["rm"],
                        "label": d_label,
                        "fstype": (dev.get("fstype") or "").strip(),
                        "mountpoint": mount_point,
                        "is_mounted": mount_point is not None,
                        "size_bytes": size_bytes,
                        "size_human": _format_size(size_bytes),
                        "free_bytes": free_bytes,
                        "free_human": _format_size(free_bytes),
                        "used_percent": used_percent,
                        "is_usb": True,
                        "is_dev_repo": False,
                    })
    except Exception as e:
        print(f"[usb_detector] Greška pri očitavanju lsblk: {e}", file=sys.stderr)

    return drives


def detect_windows_usb_drives():
    """
    Očitava USB uređaje na Windows operativnom sistemu koristeći PowerShell CIM upite,
    uz obaveznu blokadu SystemDrive-a.
    """
    drives = []
    sys_drive = os.environ.get("SystemDrive", "C:").upper().rstrip("\\")
    ps_cmd = (
        "Get-CimInstance Win32_LogicalDisk | Where-Object { $_.DriveType -eq 2 } | "
        "Select-Object DeviceID, VolumeName, VolumeSerialNumber, FileSystem, Size, FreeSpace | ConvertTo-Json -Compress"
    )
    try:
        proc = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command", ps_cmd],
            capture_output=True, text=True, timeout=8
        )
        if proc.returncode == 0 and proc.stdout.strip():
            raw = proc.stdout.strip()
            data = json.loads(raw)
            if isinstance(data, dict):
                data = [data]
            for item in data:
                dev_id = (item.get("DeviceID") or "").strip().upper()
                if not dev_id or dev_id == sys_drive:
                    continue
                size_bytes = int(item.get("Size") or 0)
                free_bytes = int(item.get("FreeSpace") or 0)
                used_pct = round(((size_bytes - free_bytes) / size_bytes) * 100, 1) if size_bytes > 0 else 0
                mount = dev_id + "\\"
                vol_serial = (item.get("VolumeSerialNumber") or "").strip()
                label = item.get("VolumeName") or "Removable Disk"
                drives.append({
                    "device_id": _build_device_id(dev_id, uuid=vol_serial, label=label),
                    "device": dev_id,
                    "parent_device": dev_id,
                    "model": "USB Removable Drive",
                    "serial": vol_serial,
                    "uuid": vol_serial,
                    "tran": "usb",
                    "rm": True,
                    "label": label,
                    "fstype": item.get("FileSystem") or "",
                    "mountpoint": mount,
                    "is_mounted": True,
                    "size_bytes": size_bytes,
                    "size_human": _format_size(size_bytes),
                    "free_bytes": free_bytes,
                    "free_human": _format_size(free_bytes),
                    "used_percent": used_pct,
                    "is_usb": True,
                    "is_dev_repo": False,
                })
    except Exception as e:
        print(f"[usb_detector] Greška pri Windows CIM upitu: {e}", file=sys.stderr)
    return drives


def get_all_usb_drives(include_project_repo=False):
    """
    Vraća listu svih detektovanih i verifikovanih USB drajvova.
    Ako je include_project_repo True, dodaje lokalni repozitorijum projekta isključivo
    kao označeni razvojni cilj ('is_dev_repo': True) za ne-destruktivno testiranje skripti.
    """
    is_windows = platform.system() == "Windows"
    if is_windows:
        drives = detect_windows_usb_drives()
    else:
        drives = detect_linux_usb_drives()

    if include_project_repo:
        project_root = _get_project_root()
        has_sym, _ = _has_symlink_in_path(str(project_root))
        forb, _ = _is_forbidden_path(str(project_root))
        if not has_sym and not forb and project_root.exists() and project_root.is_dir():
            try:
                du = shutil.disk_usage(project_root)
                drives.append({
                    "device_id": "PROJEKAT_REPOZITORIJUM",
                    "device": "PROJEKAT_REPOZITORIJUM",
                    "parent_device": "PROJEKAT_LOCAL",
                    "model": "Lokalni GWADT Repozitorijum (Razvojni Režim)",
                    "serial": "DEV_REPO_LOCAL",
                    "uuid": "DEV_REPO_LOCAL",
                    "tran": "local_dev",
                    "rm": False,
                    "label": "GWADT_DEV_REPO",
                    "fstype": "ext4/local",
                    "mountpoint": str(project_root),
                    "is_mounted": True,
                    "size_bytes": du.total,
                    "size_human": _format_size(du.total),
                    "free_bytes": du.free,
                    "free_human": _format_size(du.free),
                    "used_percent": round(((du.total - du.free) / du.total) * 100, 1) if du.total else 0,
                    "is_usb": False,
                    "is_dev_repo": True,
                })
            except Exception:
                pass

    return drives


# ==============================================================================
# 5. AUTORITATIVNA SERVER-SIDE VALIDACIJA CILJNOG USB UREĐAJA
# ==============================================================================

def resolve_and_validate_usb_target(
    target_id_or_path,
    operation="sync",
    allow_dev_repo=False,
    expected_identity=None,
):
    """
    Autoritativna server-side validacija i razrešavanje ciljnog USB uređaja.
    Neposredno pre svake operacije:
    1. Odbija prazne ciljeve i bilo kakvo symlink preusmeravanje (os.path.realpath != abspath).
    2. Apsolutno blokira sistemske i mrežne putanje (/, /boot, /boot/efi, /home, /usr, /var,
       /etc, /tmp, /mnt/TK_NAS) i sve blok uređaje na kojima je root/boot/home particija hosta
       (čak i kada je allow_dev_repo=True!).
    3. Razdvaja --dev režim: lokalni projektni folder je dozvoljen isključivo za ne-destruktivno
       kopiranje skripti (operation='sync'), a strogo zabranjen za eject, clear_dirty, formatiranje i brisanje.
    4. Osvežava serverski popis uređaja (get_all_usb_drives) i cilj razrešava ISKLJUČIVO iz
       sopstvenog verifikovanog popisa (nikada ne veruje slepo klijentskom stringu).
    5. Ponovo proverava fizički uređaj (USB transport, rm/USB SSD identitet, stvarnu tačku montiranja
       i poklapanje serial/uuid/model identiteta).

    Vraća (True, resolved_target_dict) ili (False, error_message_str).
    """
    op_norm = str(operation or "sync").strip().lower()

    # Izvlačenje sirovog identifikatora i očekivanog identiteta ako je prosleđen dict
    if isinstance(target_id_or_path, dict):
        raw_target = (
            target_id_or_path.get("device_id")
            or target_id_or_path.get("mountpoint")
            or target_id_or_path.get("device")
            or ""
        )
        if expected_identity is None:
            expected_identity = {
                k: target_id_or_path.get(k)
                for k in ("serial", "uuid", "model", "device", "mountpoint")
                if target_id_or_path.get(k)
            }
    else:
        raw_target = str(target_id_or_path or "").strip()

    raw_target = str(raw_target).strip()
    if not raw_target:
        return False, "Odbijeno: Nije naveden identifikator ili putanja ciljnog USB uređaja."

    # 1. Provera symlink preusmeravanja na ulaznom stringu
    has_sym, sym_reason = _has_symlink_in_path(raw_target)
    if has_sym:
        return False, f"Odbijeno (Symlink zaštita): {sym_reason}"

    if isinstance(expected_identity, dict):
        for k_path in ("device", "mountpoint"):
            val_p = expected_identity.get(k_path)
            if val_p:
                sym_k, sym_k_reason = _has_symlink_in_path(val_p)
                if sym_k:
                    return False, f"Odbijeno (Symlink zaštita u '{k_path}'): {sym_k_reason}"

    # 2. Apsolutna blokada sistemskih i mrežnih diskova i putanja (čak i kada je --dev uključen!)
    system_devices, network_mounts = _get_system_and_network_guard_data()
    is_forb, forb_reason = _is_forbidden_path(raw_target, system_devices, network_mounts)
    if is_forb:
        return False, f"Odbijeno (Zaštita sistemskog/mrežnog diska): {forb_reason}"

    if isinstance(expected_identity, dict):
        for k_path in ("device", "mountpoint"):
            val_p = expected_identity.get(k_path)
            if val_p:
                forb_k, forb_k_reason = _is_forbidden_path(val_p, system_devices, network_mounts)
                if forb_k:
                    return False, f"Odbijeno (Zaštita sistemskog/mrežnog diska): {forb_k_reason}"

    # 3. Pravilo za --dev režim i lokalni projektni repozitorijum
    project_root_str = str(_get_project_root())
    is_targeting_dev_repo = (
        raw_target == "PROJEKAT_REPOZITORIJUM"
        or os.path.abspath(os.path.normpath(raw_target)) == project_root_str
        or (
            isinstance(expected_identity, dict)
            and (
                expected_identity.get("device") == "PROJEKAT_REPOZITORIJUM"
                or expected_identity.get("mountpoint") == project_root_str
            )
        )
    )

    if is_targeting_dev_repo:
        if op_norm in DEV_REPO_FORBIDDEN_OPERATIONS or op_norm not in DEV_REPO_ALLOWED_OPERATIONS:
            return (
                False,
                f"Odbijeno: Operacija '{op_norm}' je strogo zabranjena nad lokalnim razvojnim repozitorijumom "
                f"(--dev režim dozvoljava isključivo ne-destruktivni test kopiranja skripti)!",
            )
        if not allow_dev_repo:
            return (
                False,
                "Odbijeno: Rad sa lokalnim razvojnim repozitorijumom nije dozvoljen jer server nije pokrenut u --dev režimu.",
            )

    # Za destruktivne/disk operacije nikada ne uključujemo projektni repo u popis kandidata
    effective_allow_dev = bool(allow_dev_repo and op_norm in DEV_REPO_ALLOWED_OPERATIONS)

    # 4. Osvežavanje serverskog popisa stvarnih uređaja i pretraga ISKLJUČIVO u tom popisu
    verified_drives = get_all_usb_drives(include_project_repo=effective_allow_dev)

    matched_drive = None
    for d in verified_drives:
        if raw_target in (d.get("device_id"), d.get("device"), d.get("mountpoint")):
            matched_drive = d
            break
        # Poredjenje po kanonskom mountpointu ako nije symlink
        d_mp = d.get("mountpoint")
        if d_mp and os.path.exists(d_mp) and raw_target.startswith("/"):
            if os.path.realpath(d_mp) == os.path.abspath(os.path.normpath(raw_target)):
                matched_drive = d
                break

    if not matched_drive:
        return (
            False,
            f"Odbijeno: Cilj '{raw_target}' nije pronađen u verifikovanom serverskom popisu priključenih USB uređaja!",
        )

    # 5. Provera identiteta uređaja ako je klijent prosledio očekivani serial/uuid/model
    if isinstance(expected_identity, dict) and expected_identity:
        for id_field in ("serial", "uuid", "model"):
            exp_val = (expected_identity.get(id_field) or "").strip()
            act_val = (matched_drive.get(id_field) or "").strip()
            if exp_val and act_val and exp_val != act_val:
                return (
                    False,
                    f"Odbijeno: Identitet uređaja ('{id_field}') se promenio (očekivano '{exp_val}', stvarno '{act_val}')!",
                )
        for path_field in ("device", "mountpoint"):
            exp_p = (expected_identity.get(path_field) or "").strip()
            act_p = (matched_drive.get(path_field) or "").strip()
            if exp_p and act_p and exp_p != act_p:
                return (
                    False,
                    f"Odbijeno: Nepoklapanje '{path_field}' sa serverskim popisom (poslato '{exp_p}', serverski '{act_p}')!",
                )

    # 6. Završna provera za razvojni repozitorijum (kada je dozvoljen za sync)
    if matched_drive.get("is_dev_repo"):
        if not effective_allow_dev:
            return False, "Odbijeno: Razvojni repozitorijum nije dozvoljen za ovu operaciju."
        mp = matched_drive.get("mountpoint") or ""
        has_sym_mp, sym_mp_reason = _has_symlink_in_path(mp)
        if has_sym_mp:
            return False, f"Odbijeno: {sym_mp_reason}"
        real_mp = os.path.realpath(mp)
        resolved = dict(matched_drive)
        resolved["canonical_device"] = "PROJEKAT_REPOZITORIJUM"
        resolved["canonical_mountpoint"] = real_mp
        resolved["operation"] = op_norm
        resolved["lock_key"] = normalize_target_lock_key(resolved)
        return True, resolved

    # 7. Duboka verifikacija fizičkog USB uređaja neposredno pre izvršenja
    dev_path = matched_drive.get("device") or ""
    parent_path = matched_drive.get("parent_device") or dev_path
    mp_path = matched_drive.get("mountpoint") or ""

    for candidate_p in (dev_path, parent_path, mp_path):
        if candidate_p:
            sym_c, sym_c_msg = _has_symlink_in_path(candidate_p)
            if sym_c:
                return False, f"Odbijeno (Symlink na uređaju): {sym_c_msg}"
            forb_c, forb_c_msg = _is_forbidden_path(candidate_p, system_devices, network_mounts)
            if forb_c:
                return False, f"Odbijeno (Zaštićen disk): {forb_c_msg}"

    if platform.system() != "Windows":
        if not dev_path.startswith("/dev/") or not os.path.exists(dev_path):
            return False, f"Odbijeno: Fizički blok uređaj '{dev_path}' ne postoji u /dev."

        ok_hw, hw_details, hw_reason = _verify_linux_usb_hardware(
            dev_name=os.path.basename(dev_path),
            parent_name=os.path.basename(parent_path),
            tran=matched_drive.get("tran") or "",
            rm=matched_drive.get("rm", False),
            model=matched_drive.get("model") or "",
            serial=matched_drive.get("serial") or "",
            uuid=matched_drive.get("uuid") or "",
            system_devices=system_devices,
        )
        if not ok_hw:
            return False, f"Odbijeno (Hardverska USB verifikacija): {hw_reason}"

        # Za operacije koje zahtevaju montiran fajlsistem, proveri da je mountpoint stvarno montiran na taj blok uređaj
        if op_norm in {"sync", "sync_scripts", "upgrade", "upgrade_usb", "inspect", "read_log"}:
            if not mp_path or not os.path.isdir(mp_path):
                return False, f"Odbijeno: USB uređaj '{dev_path}' nema aktivnu tačku montiranja."
            real_mp = os.path.realpath(mp_path)
            if not os.path.ismount(real_mp):
                return False, f"Odbijeno: Putanja '{real_mp}' nije aktivna tačka montiranja (ismount=False)."
            try:
                chk = subprocess.run(
                    ["findmnt", "-n", "-o", "SOURCE,FSTYPE", "--target", real_mp],
                    capture_output=True,
                    text=True,
                    timeout=4,
                )
                if chk.returncode == 0 and chk.stdout.strip():
                    parts = chk.stdout.strip().split()
                    mnt_src = parts[0] if parts else ""
                    mnt_fs = parts[1].lower() if len(parts) > 1 else ""
                    if mnt_fs in NETWORK_FS_TYPES:
                        return False, f"Odbijeno: Tačka montiranja '{real_mp}' koristi mrežni fajlsistem '{mnt_fs}'."
                    if mnt_src and os.path.realpath(mnt_src) != os.path.realpath(dev_path):
                        return (
                            False,
                            f"Odbijeno: Tačka montiranja '{real_mp}' pripada uređaju '{mnt_src}', a ne '{dev_path}'!",
                        )
            except Exception:
                pass

    resolved = dict(matched_drive)
    resolved["canonical_device"] = os.path.realpath(dev_path) if dev_path.startswith("/") else dev_path
    resolved["canonical_mountpoint"] = os.path.realpath(mp_path) if (mp_path and mp_path.startswith("/")) else mp_path
    resolved["operation"] = op_norm
    resolved["lock_key"] = normalize_target_lock_key(resolved)
    return True, resolved


# ==============================================================================
# 6. OPERACIJE NAD FIZIČKIM USB UREĐAJIMA (EJECT & CLEAR DIRTY BIT)
# ==============================================================================

def eject_usb_drive(mountpoint_or_device):
    """
    Bezbedno demontira i priprema USB uređaj za fizičko uklanjanje.
    Obavezno izvršava resolve_and_validate_usb_target sa allow_dev_repo=False.
    """
    if not mountpoint_or_device:
        return False, "Nije zadata putanja ili uređaj za demontiranje."

    ok_val, resolved = resolve_and_validate_usb_target(
        mountpoint_or_device,
        operation="eject",
        allow_dev_repo=False,
    )
    if not ok_val:
        return False, resolved

    target_dev = resolved.get("canonical_device") or resolved.get("device")
    target_mp = resolved.get("canonical_mountpoint") or resolved.get("mountpoint")

    is_windows = platform.system() == "Windows"
    if is_windows:
        drive_letter = str(target_dev).replace("\\", "").replace(":", "")[:1].upper()
        if not drive_letter.isalnum():
            return False, "Neispravna oznaka diska na Windows-u."
        ps_cmd = (
            f"$drive = '{drive_letter}:'; "
            f"(New-Object -comObject Shell.Application).Namespace(17).ParseName($drive).InvokeVerb('Eject')"
        )
        try:
            res = subprocess.run(
                ["powershell.exe", "-NoProfile", "-Command", ps_cmd],
                capture_output=True, text=True, timeout=10
            )
            return (
                res.returncode == 0,
                "Komanda za izbacivanje poslata Windows sistemu." if res.returncode == 0 else res.stderr,
            )
        except Exception as e:
            return False, str(e)
    else:
        try:
            subprocess.run(["sync"], timeout=5)
        except Exception:
            pass

        if target_mp and os.path.exists(target_mp) and os.path.isdir(target_mp):
            res = subprocess.run(["udisksctl", "unmount", "-b", target_dev], capture_output=True, text=True, timeout=15)
            if res.returncode == 0:
                return True, f"Uređaj {target_dev} je bezbedno demontiran."
            res2 = subprocess.run(["umount", target_mp], capture_output=True, text=True, timeout=15)
            return (
                res2.returncode == 0,
                "Demontirano." if res2.returncode == 0 else (res.stderr or res2.stderr),
            )
        else:
            res = subprocess.run(["udisksctl", "unmount", "-b", target_dev], capture_output=True, text=True, timeout=15)
            return (res.returncode == 0, "Demontirano." if res.returncode == 0 else res.stderr)


def clear_dirty_bit(device_or_mountpoint):
    """
    Čisti 'dirty bit' i popravlja fajl sistem na verifikovanom USB fleš uređaju.
    Obavezno izvršava resolve_and_validate_usb_target sa allow_dev_repo=False.
    """
    if not device_or_mountpoint:
        return False, "Nije naveden uređaj ili tačka montiranja."

    ok_val, resolved = resolve_and_validate_usb_target(
        device_or_mountpoint,
        operation="clear_dirty",
        allow_dev_repo=False,
    )
    if not ok_val:
        return False, resolved

    target_dev = resolved.get("canonical_device") or resolved.get("device")

    is_windows = platform.system() == "Windows"
    if is_windows:
        drive_letter = str(target_dev).replace("\\", "").replace(":", "")[:1].upper()
        if not drive_letter.isalnum():
            return False, "Neispravna oznaka diska na Windows-u."
        ps_cmd = f"fsutil dirty query {drive_letter}: ; chkdsk {drive_letter}: /f /x"
        try:
            res = subprocess.run(
                ["powershell.exe", "-NoProfile", "-Command", ps_cmd],
                capture_output=True, text=True, timeout=30
            )
            return (res.returncode == 0, "Windows chkdsk završen." if res.returncode == 0 else res.stderr)
        except Exception as e:
            return False, str(e)
    else:
        dev_name = os.path.basename(str(target_dev))
        if not dev_name or not os.path.exists(f"/dev/{dev_name}"):
            return False, f"Nije pronađen blok uređaj za: {target_dev}"

        was_mounted = False
        try:
            chk = subprocess.run(["findmnt", "-n", f"/dev/{dev_name}"], capture_output=True, text=True, timeout=5)
            if chk.returncode == 0 and chk.stdout.strip():
                was_mounted = True
                subprocess.run(
                    ["udisksctl", "unmount", "-b", f"/dev/{dev_name}"],
                    capture_output=True, text=True, timeout=15
                )
        except Exception:
            pass

        obj_path = f"/org/freedesktop/UDisks2/block_devices/{dev_name}"
        dbus_cmd = [
            "gdbus", "call", "--system",
            "--dest", "org.freedesktop.UDisks2",
            "--object-path", obj_path,
            "--method", "org.freedesktop.UDisks2.Filesystem.Repair",
            "{}"
        ]

        try:
            res = subprocess.run(dbus_cmd, capture_output=True, text=True, timeout=30)
            repair_ok = res.returncode == 0 and "(true,)" in res.stdout

            if was_mounted or repair_ok:
                subprocess.run(
                    ["udisksctl", "mount", "-b", f"/dev/{dev_name}"],
                    capture_output=True, text=True, timeout=10
                )

            if repair_ok:
                return True, f"Dirty bit uspešno očišćen za /dev/{dev_name} preko UDisks2 i ponovo montiran."
            else:
                err_msg = res.stderr.strip() or res.stdout.strip()
                return False, f"Neuspešno čišćenje: {err_msg}"
        except Exception as e:
            if was_mounted:
                subprocess.run(
                    ["udisksctl", "mount", "-b", f"/dev/{dev_name}"],
                    capture_output=True, text=True, timeout=10
                )
            return False, f"Greška: {str(e)}"
