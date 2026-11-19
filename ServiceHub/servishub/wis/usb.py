"""
servishub.wis.usb
=================
Upravljanje prepoznavanjem, bezbednosnom proverom i UEFI formatiranjem
USB prenosivih diskova za Windows Installation Server (WIS).
"""

import json
import logging
import os
import re
import shutil
import subprocess
import time
from typing import Any, Callable, Dict, List, Optional, Set

logger = logging.getLogger("servishub.wis.usb")

# Kritične sistemske putanje koje se NIKADA ne smeju nalaziti na formatiranom disku
CRITICAL_SYSTEM_MOUNTS: Set[str] = {
    "/",
    "/boot",
    "/boot/efi",
    "/home",
    "/var",
    "/etc",
    "/usr",
    "/root",
    "/opt",
    "/bin",
    "/sbin",
    "/lib",
    "/lib64",
}


def format_bytes(size_bytes: int) -> str:
    """Formatira broj bajtova u čitljivu jedinicu (B, KiB, MiB, GiB, TiB)."""
    if size_bytes <= 0:
        return "0 B"
    val = float(size_bytes)
    for unit in ["B", "KiB", "MiB", "GiB", "TiB"]:
        if val < 1024.0 or unit == "TiB":
            if unit == "B":
                return f"{int(val)} {unit}"
            return f"{val:.1f} {unit}"
        val /= 1024.0
    return f"{val:.1f} GiB"


def get_partition_device_path(disk_path: str, part_num: int) -> str:
    """
    Vraća putanju do particije za dati disk.
    Primer: /dev/sdb -> /dev/sdb1, /dev/nvme0n1 -> /dev/nvme0n1p1, /dev/loop0 -> /dev/loop0p1
    """
    clean_disk = disk_path.rstrip("/")
    if re.search(r"\d$", clean_disk):
        return f"{clean_disk}p{part_num}"
    return f"{clean_disk}{part_num}"


def get_active_zpool_devices(runner: Optional[Callable] = None) -> Set[str]:
    """
    Detektuje uređaje koji pripadaju aktivnim ZFS pool-ovima.
    Vraća skup naziva i putanja uređaja u zpool-u.
    """
    run_fn = runner or subprocess.run
    zpool_devs: Set[str] = set()

    # Provera preko zpool status
    try:
        res = run_fn(
            ["zpool", "status"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            check=False,
            timeout=3,
        )
        if res.returncode == 0 and res.stdout:
            for line in res.stdout.splitlines():
                stripped = line.strip()
                if not stripped or stripped.startswith("pool:") or stripped.startswith("state:"):
                    continue
                tokens = stripped.split()
                if tokens:
                    candidate = tokens[0]
                    zpool_devs.add(candidate)
                    zpool_devs.add(os.path.basename(candidate))
    except (FileNotFoundError, subprocess.SubprocessError, Exception):
        pass

    return zpool_devs


def _collect_all_mountpoints(node: Dict[str, Any]) -> List[str]:
    """Rekurzivno prikuplja sve tačke montiranja za dati blok čvor i sve njegove particije."""
    mounts: List[str] = []
    raw_mps = node.get("mountpoints")
    if isinstance(raw_mps, list):
        for mp in raw_mps:
            if mp:
                mounts.append(str(mp).strip())
    elif isinstance(raw_mps, str) and raw_mps.strip():
        mounts.append(raw_mps.strip())

    single_mp = node.get("mountpoint")
    if single_mp and str(single_mp).strip() not in mounts:
        mounts.append(str(single_mp).strip())

    for child in node.get("children") or []:
        mounts.extend(_collect_all_mountpoints(child))

    return mounts


def _collect_node_names(node: Dict[str, Any]) -> Set[str]:
    """Rekurzivno prikuplja nazive samog diska i svih njegovih podparticija."""
    names: Set[str] = set()
    name = node.get("name")
    if name:
        names.add(str(name).strip())
        names.add(os.path.basename(str(name).strip()))
    for child in node.get("children") or []:
        names.update(_collect_node_names(child))
    return names


def _has_system_mount(mountpoints: List[str]) -> bool:
    """Proverava da li se među tačkama montiranja nalazi bilo koja sistemska putanja."""
    for mp in mountpoints:
        clean = mp.rstrip("/")
        if clean == "" or clean in CRITICAL_SYSTEM_MOUNTS:
            return True
        for sys_dir in ("/boot/", "/etc/", "/usr/", "/var/"):
            if clean.startswith(sys_dir):
                return True
    return False


def _evaluate_device_safety(
    node: Dict[str, Any],
    all_mountpoints: List[str],
    is_removable: bool,
    is_usb: bool,
    zpool_devs: Set[str],
) -> tuple[bool, Optional[str]]:
    """
    Procena bezbednosnih uslova (Safety Guardrails):
    1. Disk i sve njegove particije NIKAKO NE SMEJU imati montirane sistemske putanje
       (/, /boot, /boot/efi, /home, /var, /etc) niti pripadati ZFS zpool-ovima.
       U tom slučaju is_safe = False, blocked_reason = "Sistemski disk / zpool".
    2. Disk mora biti prenosiv (removable == True ili hotplug == True) I transport == "usb".
    """
    # 1. Provera sistemskih particija
    if _has_system_mount(all_mountpoints):
        return False, "Sistemski disk / zpool"

    # 2. Provera ZFS zpool članstva
    node_names = _collect_node_names(node)
    for n in node_names:
        if n in zpool_devs or f"/dev/{n}" in zpool_devs:
            return False, "Sistemski disk / zpool"

    # Takođe provera fstype za zfs
    def _check_zfs_fstype(item: Dict[str, Any]) -> bool:
        fstype = str(item.get("fstype") or "").lower()
        if "zfs" in fstype:
            return True
        for ch in item.get("children") or []:
            if _check_zfs_fstype(ch):
                return True
        return False

    if _check_zfs_fstype(node):
        return False, "Sistemski disk / zpool"

    # 3. Provera USB transporta i removable zastavice
    if not is_usb or not is_removable:
        reasons = []
        if not is_usb:
            reasons.append("transport nije USB")
        if not is_removable:
            reasons.append("nije izmenjiv (removable/hotplug)")
        return False, f"Uređaj nije podržan ({', '.join(reasons)})"

    return True, None


def list_removable_usb_devices(
    include_all: bool = False,
    run_cmd: Optional[Callable] = None,
    mock_json: Optional[str] = None,
) -> List[Dict[str, Any]]:
    """
    Pronalazi i analizira priključene diskove koristeći lsblk.
    
    Safety Guardrails (is_safe):
    - Disk mora imati removable == True (RM == "1" ili 1 ili HOTPLUG == "1" ili 1) I transport == "usb".
    - Disk i sve njegove particije NIKAKO NE SMEJU imati montirane sistemske putanje:
      /, /boot, /boot/efi, /home, /var, /etc, niti smeju pripadati ZFS zpool-ovima.
    - Ako bilo koja particija ima montiran sistemski direktorijum ili pripada zpool-u,
      disk se označava sa is_safe = False i blocked_reason = "Sistemski disk / zpool".
      
    Vraća listu uređaja sa poljima:
    name, device_path, model, size_bytes, size_formatted, removable, transport, is_safe, blocked_reason.
    """
    run_fn = run_cmd or subprocess.run
    zpool_devs = get_active_zpool_devices(runner=run_fn)

    raw_json_str = mock_json or os.environ.get("SERVIS_HUB_MOCK_LSBLK_JSON")
    devices_data: List[Dict[str, Any]] = []

    if raw_json_str:
        try:
            parsed = json.loads(raw_json_str)
            devices_data = parsed.get("blockdevices", [])
        except Exception as e:
            logger.warning(f"Neuspešno parsiranje simuliranog lsblk JSON-a: {e}")
            devices_data = []
    else:
        # Primarni poziv sa svim kolonama
        cmd = ["lsblk", "-J", "-b", "-o", "NAME,SIZE,MODEL,TRAN,RM,HOTPLUG,MOUNTPOINTS,FSTYPE"]
        try:
            res = run_fn(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                check=False,
            )
            if res.returncode == 0 and res.stdout:
                parsed = json.loads(res.stdout)
                devices_data = parsed.get("blockdevices", [])
            else:
                # Fallback za starije lsblk verzije koje možda nemaju HOTPLUG ili MOUNTPOINTS
                fallback_cmd = ["lsblk", "-J", "-b", "-o", "NAME,SIZE,MODEL,TRAN,RM,MOUNTPOINT,FSTYPE"]
                fb_res = run_fn(
                    fallback_cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    check=False,
                )
                if fb_res.returncode == 0 and fb_res.stdout:
                    parsed = json.loads(fb_res.stdout)
                    devices_data = parsed.get("blockdevices", [])
        except (FileNotFoundError, json.JSONDecodeError, Exception) as e:
            logger.warning(f"lsblk nije dostupan ili je vratio grešku: {e}")
            devices_data = []

    results: List[Dict[str, Any]] = []

    for dev in devices_data:
        name = str(dev.get("name") or "").strip()
        if not name:
            continue

        # Preskačemo loop i zram virtuelne diskove osim ako include_all nije tražen
        if not include_all and (name.startswith("loop") or name.startswith("zram")):
            continue

        device_path = f"/dev/{name}" if not name.startswith("/dev/") else name
        raw_model = dev.get("model")
        model = str(raw_model).strip() if raw_model else "Generic USB Device"

        try:
            size_bytes = int(dev.get("size") or 0)
        except (ValueError, TypeError):
            size_bytes = 0

        size_formatted = format_bytes(size_bytes)

        rm_val = dev.get("rm")
        hotplug_val = dev.get("hotplug")
        is_removable = (
            rm_val is True
            or rm_val == 1
            or rm_val == "1"
            or hotplug_val is True
            or hotplug_val == 1
            or hotplug_val == "1"
        )

        raw_tran = dev.get("tran")
        transport = str(raw_tran).lower().strip() if raw_tran else None
        is_usb = transport == "usb"

        all_mountpoints = _collect_all_mountpoints(dev)

        is_safe, blocked_reason = _evaluate_device_safety(
            node=dev,
            all_mountpoints=all_mountpoints,
            is_removable=is_removable,
            is_usb=is_usb,
            zpool_devs=zpool_devs,
        )

        # Filtriranje ako nije include_all: prikazujemo USB ili prenosive uređaje
        if not include_all and not (is_usb or is_removable):
            continue

        device_info = {
            "name": name,
            "device_path": device_path,
            "model": model,
            "size_bytes": size_bytes,
            "size_formatted": size_formatted,
            "removable": is_removable,
            "transport": transport,
            "is_safe": is_safe,
            "blocked_reason": blocked_reason,
            "mountpoints": all_mountpoints,
        }
        results.append(device_info)

    return results


def inspect_device(
    device_path: str,
    run_cmd: Optional[Callable] = None,
    mock_json: Optional[str] = None,
) -> Optional[Dict[str, Any]]:
    """Pronalazi i analizira specifični uređaj po njegovoj putanji (npr. /dev/sdb)."""
    clean_target = os.path.basename(device_path.rstrip("/"))
    all_devs = list_removable_usb_devices(include_all=True, run_cmd=run_cmd, mock_json=mock_json)
    for dev in all_devs:
        if dev["name"] == clean_target or dev["device_path"] == device_path:
            return dev
    return None


class WisUsbEngine:
    """
    Engine za pripremu, particionisanje i kreiranje UEFI instalacionih USB medija.
    
    Kreira GPT particionu tabelu:
    - Particija 1 (FAT32): UEFI Bootloader (1-2 GB, ESP/boot flag)
    - Particija 2 (NTFS): WIS Data / Windows Install WIM i veliki alati (ostatak diska)
    """

    def __init__(self, dry_run: bool = False, run_cmd: Optional[Callable] = None):
        self.dry_run = dry_run
        self.run_cmd = run_cmd or subprocess.run

    def format_uefi_usb(
        self,
        device_path: str,
        confirm_wipe: bool = False,
        progress_callback: Optional[Callable[[Dict[str, Any]], None]] = None,
        fat32_size_mb: int = 2048,
        mock_json: Optional[str] = None,
    ) -> Dict[str, Any]:
        """
        Particioniše i formatira USB uređaj za UEFI butovanje sa podrškom za velike fajlove.
        
        Zahteva:
        - confirm_wipe == True (striktna zaštita od slučajnog brisanja)
        - is_safe == True (provera da uređaj nije sistemski ili zpool)
        
        Emituje korake preko progress_callback({"phase": ..., "percent": ..., "message": ...})
        """
        def emit(phase: str, percent: int, message: str) -> None:
            if progress_callback:
                progress_callback({
                    "phase": phase,
                    "percent": percent,
                    "message": message,
                    "device_path": device_path,
                })

        # 1. Striktna provera confirm_wipe
        if not confirm_wipe:
            raise ValueError(
                f"Formatiranje uređaja {device_path} zahteva striktnu potvrdu: confirm_wipe=True."
            )

        # 2. Bezbednosna verifikacija uređaja
        dev = inspect_device(device_path, run_cmd=self.run_cmd, mock_json=mock_json)
        if not dev:
            raise ValueError(f"Uređaj {device_path} nije pronađen na sistemu.")

        if not dev.get("is_safe"):
            reason = dev.get("blocked_reason") or "Uređaj nije bezbedan za formatiranje"
            raise RuntimeError(f"Formatiranje uređaja {device_path} je blokirano: {reason}")

        part1_path = get_partition_device_path(device_path, 1)
        part2_path = get_partition_device_path(device_path, 2)

        # 3. Faza: Demontiranje postojećih particija
        emit("unmounting", 10, f"Demontiranje aktivnih particija na {device_path}...")
        for mp in dev.get("mountpoints") or []:
            self._execute(["umount", "-l", mp], ignore_errors=True)
        # Takođe pokušaj unmount za uobičajene podparticije
        self._execute(["umount", "-l", part1_path], ignore_errors=True)
        self._execute(["umount", "-l", part2_path], ignore_errors=True)

        # 4. Faza: Brisanje starih potpisa i tabela (wipefs)
        emit("wiping", 25, f"Uklanjanje starih particionih tabela i potpisa (wipefs) sa {device_path}...")
        self._execute(["wipefs", "--all", "--force", device_path])

        # 5. Faza: Kreiranje GPT particione tabele
        emit(
            "partitioning",
            50,
            f"Kreiranje GPT tabele: FAT32 UEFI Boot ({fat32_size_mb}MB) + NTFS WIS Data (ostatak)...",
        )
        self._partition_gpt(device_path, fat32_size_mb=fat32_size_mb)

        # Pauza za osvežavanje tabele particija u kernelu
        self._settle_partitions(device_path)

        # 6. Faza: Formatiranje FAT32 particije
        emit("formatting_fat32", 75, f"Formatiranje UEFI Boot particije (FAT32) na {part1_path}...")
        self._format_fat32(part1_path, label="UEFI_BOOT")

        # 7. Faza: Formatiranje NTFS particije
        emit("formatting_ntfs", 90, f"Formatiranje WIS Data particije (NTFS) na {part2_path}...")
        self._format_ntfs(part2_path, label="WIS_DATA")

        # 8. Faza: Završeno
        emit("completed", 100, f"UEFI USB na {device_path} je uspešno formatiran.")

        return {
            "status": "success",
            "device_path": device_path,
            "fat32_partition": part1_path,
            "ntfs_partition": part2_path,
            "fat32_label": "UEFI_BOOT",
            "ntfs_label": "WIS_DATA",
            "table": "gpt",
        }

    def _execute(self, cmd: List[str], ignore_errors: bool = False) -> None:
        """Izvršava sistemsku komandu ili je simulira u dry_run režimu."""
        cmd_str = " ".join(cmd)
        if self.dry_run:
            logger.info(f"[DRY-RUN] Izvršavam: {cmd_str}")
            return

        try:
            res = self.run_cmd(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                check=False,
            )
            if res.returncode != 0 and not ignore_errors:
                err_msg = res.stderr.strip() if res.stderr else f"Exit code {res.returncode}"
                raise RuntimeError(f"Greška pri izvršavanju komande '{cmd_str}': {err_msg}")
        except FileNotFoundError as e:
            if not ignore_errors:
                raise RuntimeError(f"Alatka nije pronađena ({cmd[0]}): {e}")

    def _partition_gpt(self, device_path: str, fat32_size_mb: int = 2048) -> None:
        """Kreira GPT particionu tabelu sa dve particije koristeći parted ili sgdisk."""
        # Proveri parted
        if shutil.which("parted") or self.dry_run:
            self._execute(["parted", "-s", device_path, "mklabel", "gpt"])
            self._execute([
                "parted",
                "-s",
                device_path,
                "mkpart",
                "UEFI_BOOT",
                "fat32",
                "1MiB",
                f"{fat32_size_mb}MiB",
            ])
            self._execute(["parted", "-s", device_path, "set", "1", "esp", "on"])
            self._execute([
                "parted",
                "-s",
                device_path,
                "mkpart",
                "WIS_DATA",
                "ntfs",
                f"{fat32_size_mb}MiB",
                "100%",
            ])
        elif shutil.which("sgdisk"):
            # Fallback na sgdisk
            self._execute(["sgdisk", "--zap-all", device_path])
            self._execute(["sgdisk", "-n", f"1:2048:+{fat32_size_mb}M", "-t", "1:ef00", "-c", "1:UEFI_BOOT", device_path])
            self._execute(["sgdisk", "-n", "2:0:0", "-t", "2:0700", "-c", "2:WIS_DATA", device_path])
        else:
            raise RuntimeError("Nijedan particioni alat nije dostupan (parted ili sgdisk).")

    def _settle_partitions(self, device_path: str) -> None:
        """Sinhronizuje particionu tabelu sa operativnim sistemom."""
        if self.dry_run:
            return
        if shutil.which("partprobe"):
            self._execute(["partprobe", device_path], ignore_errors=True)
        if shutil.which("udevadm"):
            self._execute(["udevadm", "settle", "--timeout=5"], ignore_errors=True)
        time.sleep(0.5)

    def _format_fat32(self, part_path: str, label: str = "UEFI_BOOT") -> None:
        """Formatira FAT32 particiju za UEFI Bootloader."""
        tool = "mkfs.vfat" if shutil.which("mkfs.vfat") else "mkfs.fat"
        if not shutil.which(tool) and not self.dry_run:
            raise RuntimeError("Alatka za formatiranje FAT32 (mkfs.vfat / mkfs.fat) nije pronađena.")
        self._execute([tool, "-F", "32", "-n", label, part_path])

    def _format_ntfs(self, part_path: str, label: str = "WIS_DATA") -> None:
        """Formatira NTFS particiju za WIS podatke."""
        if not shutil.which("mkfs.ntfs") and not self.dry_run:
            raise RuntimeError("Alatka za formatiranje NTFS (mkfs.ntfs) nije pronađena.")
        self._execute(["mkfs.ntfs", "-f", "-L", label, part_path])
