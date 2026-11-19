#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
================================================================================
WIS (Windows Installation Assistant) - Ventoy Bootable ISO Generator
================================================================================
Skripte/Build-Iso.py

Generiše butabilnu hibridnu (BIOS + UEFI) Windows 11 ISO sliku optimizovanu za
korišćenje na Ventoy fleš medijumu ili direktno narezivanje / mountovanje.

Ključne mogućnosti:
1. Hard-link staging mehanizam: formira stablo ISO slike u deliću sekunde
   bez zauzimanja dodatnog prostora na disku (fallback na shutil.copy2).
2. Automatsko uklanjanje "Press any key..." prompta (no-prompt boot)
   za potpuno automatizovan i čist prolaz kroz Windows Setup.
3. Hibridni xorriso motor ('auto', 'host', 'docker'):
   - Host xorriso ili automatski mikro-kontejner 'wis-iso-builder:latest' (alpine+xorriso).
   - Potpuna eliminacija genisoimage/mkisofs zbog nedostatka UEFI El Torito 0xEF podrške.
4. Automatska revizija El Torito Boot Kataloga (verifikacija BIOS 0x00 i UEFI 0xEF unosa).
5. Provera slobodnog prostora na disku (minimum 14 GB za ISO sliku).
6. Izračunavanje MD5 i SHA-256 kontrolnih suma generisanog ISO-a.
"""

import os
import sys
import time
import json
import shutil
import struct
import hashlib
import argparse
import tempfile
import subprocess
from pathlib import Path
from typing import Optional, Callable, Dict, Any, Tuple, List

# Određivanje korena projekta
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = Path(os.environ.get("WIS_PROJECT_ROOT", str(SCRIPT_DIR.parent))).resolve()

# Automatska podrška za dodatne alate u .workspace/artifacts/tools i /mnt/nvme
extra_bins = [
    PROJECT_ROOT.parent / ".workspace/artifacts/tools/genisoimage",
    PROJECT_ROOT.parent / ".workspace/artifacts/tools/xorriso",
    PROJECT_ROOT.parent / ".workspace/artifacts/tools/wimlib",
    Path("/mnt/nvme/usr/bin"),
]
valid_bins = [str(p) for p in extra_bins if p.exists()]
if valid_bins:
    os.environ["PATH"] = ":".join(valid_bins) + ":" + os.environ.get("PATH", "")
extra_libs = [
    str(PROJECT_ROOT.parent / ".workspace/artifacts/tools/wimlib"),
    "/mnt/nvme/usr/lib/x86_64-linux-gnu",
    "/mnt/nvme/lib/x86_64-linux-gnu",
]
valid_libs = [p for p in extra_libs if Path(p).exists()]
if valid_libs:
    cur_ld = os.environ.get("LD_LIBRARY_PATH", "")
    os.environ["LD_LIBRARY_PATH"] = ":".join(valid_libs) + (f":{cur_ld}" if cur_ld else "")

# Nazivi top-level fajlova i direktorijuma koji se uključuju u ISO
INCLUDED_ROOT_ITEMS = [
    "boot",
    "efi",
    "sources",
    "autounattend.xml",
    "FirstLogon.cmd",
    "Wifi-Servis.ini",
    "version.json",
    "Za disk 10+ gen",
    "$WinPEDriver$",
    "setup.exe",
    "setup.dll",
    "bootmgr",
    "bootmgr.efi",
    "bootmgfw.efi",
    "autorun.inf",
    "WIS.Launcher.exe",
    "WIS.UI.exe",
    "WIS-AutopilotAuditor.exe",
    "Provera_autopilota.ps1",
    "load_vmd.cmd",
    "WIS-AcceleratePE.cmd",
    "WIS-ConnectNAS.cmd",
    "Programi.tar",
]

# Direktorijumi i obrasci koji se striktno ISKLJUČUJU iz ISO slike
EXCLUDED_NAMES = {
    "Programi",
    "Zakrpe",
    ".git",
    ".agents",
    "second_brain",
    "wiki",
    ".claude",
    "USB Tool",
    "Logs",
    "servis_deploy.log",
    "istorija_izrade.md",
    "__chunk_data",
    "System Volume Information",
    "__pycache__",
    ".pytest_cache",
    ".staging_iso_tmp",
    "support",
    "boot.wim.bak",
}


def read_target_build(project_root: Path) -> str:
    """Čita target os build iz version.json u korenu projekta."""
    version_file = project_root / "version.json"
    if not version_file.exists():
        version_file = project_root.parent / "version.json"
    if version_file.exists():
        try:
            with open(version_file, "r", encoding="utf-8") as f:
                data = json.load(f)
                build = data.get("os_build_target")
                if build:
                    return str(build).strip()
        except Exception as e:
            print(f"[UPOZORENJE] Greška pri čitanju version.json: {e}", file=sys.stderr)
    return "10.0.26200.8037"


def get_default_output_path(project_root: Path, profile: str = "lean") -> Path:
    """
    Određuje podrazumevanu lokaciju za izlazni ISO fajl (podrazumevano ~/Downloads).
    U 'lean' profilu podrazumevani naziv je 'WIS-Win11-Pro-<build>-Lean.iso',
    dok je u 'full' profilu standardni 'WIS-Win11-Pro-<build>.iso'.
    """
    target_build = read_target_build(project_root)
    suffix = "-Lean" if profile == "lean" else ""
    iso_name = f"WIS-Win11-Pro-{target_build}{suffix}.iso"

    host_downloads = os.environ.get("WIS_HOST_DOWNLOADS")
    if host_downloads and os.path.isdir(host_downloads):
        return Path(host_downloads) / iso_name

    downloads_dir = Path.home() / "Downloads"
    if downloads_dir.exists() and downloads_dir.is_dir():
        return downloads_dir / iso_name

    return project_root / iso_name


def check_disk_space(target_path: Path, min_gb: float = 9.0) -> Tuple[bool, float, float]:
    """
    Proverava slobodan prostor na particiji gde se nalazi ciljni fajl.
    Vraća (dovoljno_prostora, slobodno_gb, ukupno_gb).
    """
    target_dir = target_path if target_path.is_dir() else target_path.parent
    try:
        target_dir.mkdir(parents=True, exist_ok=True)
    except Exception:
        pass

    stat = shutil.disk_usage(str(target_dir))
    free_gb = stat.free / (1024 ** 3)
    total_gb = stat.total / (1024 ** 3)
    return (free_gb >= min_gb, free_gb, total_gb)


DOCKER_IMAGE_NAME = "wis-iso-builder:latest"
DOCKERFILE_CONTENT = """FROM alpine:latest
RUN apk add --no-cache xorriso
"""


def ensure_docker_image(docker_bin: str = "docker", image_name: str = DOCKER_IMAGE_NAME, log_cb: Optional[Callable[[str], None]] = None) -> bool:
    """
    Proverava da li postoji Docker slika za xorriso. Ako ne postoji, automatski je brzo kreira.
    """
    def _log(msg: str):
        if log_cb:
            log_cb(msg)
        else:
            print(msg)

    # Provera postojanja slike
    check_cmd = [docker_bin, "image", "inspect", image_name]
    res = subprocess.run(check_cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if res.returncode == 0:
        return True

    _log(f"🐳 Docker slika '{image_name}' nije pronađena. Započinjem automatsku pripremu (alpine + xorriso)...")
    build_cmd = [docker_bin, "build", "-t", image_name, "-"]
    build_proc = subprocess.run(
        build_cmd,
        input=DOCKERFILE_CONTENT.encode("utf-8"),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False
    )
    if build_proc.returncode != 0:
        err_out = build_proc.stdout.decode("utf-8", errors="replace") if build_proc.stdout else "Nepoznata greška"
        raise RuntimeError(f"Neuspešno automatsko kreiranje Docker slike '{image_name}':\n{err_out}")

    _log(f"✅ Docker slika '{image_name}' je uspešno pripremljena.")
    return True


def detect_iso_engine(requested_engine: str = "auto") -> Tuple[str, str]:
    """
    Pronalazi dostupan ISO generator u sistemu.
    Podržani motori:
      - 'auto': Primarni izbor je 'genisoimage' (ili 'mkisofs') sa UDF 1.02/2.00 i UEFI 0xEF podrškom (neophodno za iVentoy PXE i Windows Setup).
                Fallback na 'host_xorriso' ili 'docker_xorriso'.
      - 'genisoimage' / 'udf' / 'mkisofs': Koristi genisoimage/mkisofs za UDF 1.02/2.00 + UEFI 0xEF generisanje.
      - 'host' / 'host_xorriso' / 'xorriso': Koristi host xorriso (samo ISO9660/Joliet).
      - 'docker' / 'docker_xorriso': Koristi Docker xorriso.
    Vraća (naziv_motora, putanja_do_binarke).
    """
    req = requested_engine.strip().lower()

    if req in ["genisoimage", "udf", "mkisofs"]:
        for bin_name in ["genisoimage", "mkisofs"]:
            path = shutil.which(bin_name)
            if path:
                return "host_genisoimage", path
        raise FileNotFoundError(
            f"Zatraženi UDF ISO motor '{requested_engine}' (genisoimage/mkisofs) nije pronađen u PATH-u sistema.\n"
            "Instalirajte ga ('sudo apt install genisoimage') ili koristite '--engine xorriso'."
        )

    if req in ["host", "host_xorriso", "xorriso"]:
        path = shutil.which("xorriso")
        if path:
            return "host_xorriso", path
        raise FileNotFoundError(
            "Zatraženi ISO motor 'host' (xorriso) nije pronađen u PATH-u sistema.\n"
            "Instalirajte xorriso ('sudo apt install xorriso') ili izaberite Docker motor ('--engine docker')."
        )

    if req in ["docker", "docker_xorriso"]:
        path = shutil.which("docker")
        if path:
            return "docker_xorriso", path
        raise FileNotFoundError(
            "Zatraženi ISO motor 'docker' nije dostupan (docker binarka nije pronađena u PATH-u).\n"
            "Instalirajte Docker ili instalirajte lokalni xorriso ('sudo apt install xorriso')."
        )

    if req == "auto":
        # 1. genisoimage / mkisofs ima primarni prioritet jer donosi UDF 1.02/2.00 i UEFI 0xEF za iVentoy PXE i Ventoy USB
        for bin_name in ["genisoimage", "mkisofs"]:
            path = shutil.which(bin_name)
            if path:
                return "host_genisoimage", path

        # 2. Host xorriso (fallback ako genisoimage nije instaliran)
        xorriso_path = shutil.which("xorriso")
        if xorriso_path:
            return "host_xorriso", xorriso_path

        # 3. Docker xorriso fallback
        docker_path = shutil.which("docker")
        if docker_path:
            return "docker_xorriso", docker_path

        # 4. Nijedan motor nije pronađen
        raise FileNotFoundError(
            "Nije pronađen nijedan podržan ISO engine (genisoimage, host xorriso ili Docker xorriso) u PATH-u.\n"
            "Instalirajte genisoimage ('sudo apt install genisoimage') ili xorriso ('sudo apt install xorriso')."
        )

    raise ValueError(
        f"Nepoznat ISO motor '{requested_engine}'. Dozvoljeni motori su: 'auto', 'genisoimage', 'udf', 'host', 'docker', 'xorriso'."
    )


def audit_boot_catalog(iso_path: Path) -> Tuple[bool, str]:
    """
    Čita i analizira El Torito boot katalog iz generisanog ISO fajla.
    Pronalazi sektor kataloga iz El Torito Volume Descriptor-a (obično sektor 17 / ofset 0x8800)
    i parsira zaglavlja:
      1) Proverava Validation Entry (Header ID 0x01, Platform ID 0x00, Key 0x55AA).
      2) Proverava sekcije i traži Platform ID 0xEF (EFI/UEFI).
    Vraća:
      (True, "Potvrđeni UEFI (0xEF) i BIOS (0x00) boot unosi") ako pronađe 0xEF zapis.
      (False, "Nedostaje UEFI El Torito zapis (Platform ID 0xEF)!") ako NE pronađe 0xEF zapis.
    """
    if not iso_path.exists():
        return False, f"ISO fajl ne postoji: {iso_path}"

    SECTOR_SIZE = 2048
    catalog_sector = None

    try:
        with open(iso_path, "rb") as f:
            # 1. Pronalaženje Boot Record Volume Descriptor-a (skeniramo sektore 16..32)
            for sec_idx in range(16, 32):
                f.seek(sec_idx * SECTOR_SIZE)
                sec_data = f.read(SECTOR_SIZE)
                if len(sec_data) < SECTOR_SIZE:
                    break

                vd_type = sec_data[0]
                vd_id = sec_data[1:6]  # Očekivano b"CD001"

                if vd_id != b"CD001":
                    continue

                # Boot Record VD ima tip 0x00 i potpis "EL TORITO SPECIFICATION" na ofsetu 7
                if vd_type == 0x00 and b"EL TORITO SPECIFICATION" in sec_data[7:39]:
                    # Ofset 71..74 (4 bajta, little-endian unsigned int) je LBA sektor Boot Kataloga
                    catalog_sector = struct.unpack("<I", sec_data[71:75])[0]
                    break

                if vd_type == 0xFF:  # Volume Descriptor Set Terminator
                    break

            if catalog_sector is None:
                return False, "Nije pronađen El Torito Boot Record Volume Descriptor u ISO slici!"

            # 2. Čitanje Boot Kataloga
            f.seek(catalog_sector * SECTOR_SIZE)
            catalog_data = f.read(SECTOR_SIZE)
            if len(catalog_data) < 64:
                return False, "El Torito Boot Catalog je oštećen ili kraći od 64 bajta."

            # 3. Validacija Validation Entry (prvih 32 bajta)
            val_header_id = catalog_data[0]
            val_platform_id = catalog_data[1]
            val_key_bytes = catalog_data[30:32]

            if val_header_id != 0x01:
                return False, f"Nevalidan El Torito Validation Entry Header ID: {hex(val_header_id)} (očekivano 0x01)."

            if val_platform_id != 0x00:
                return False, f"Nevalidan El Torito Validation Entry Platform ID: {hex(val_platform_id)} (očekivano 0x00 za BIOS/x86)."

            if val_key_bytes != b"\x55\xaa":
                return False, f"Nevalidni El Torito ključni bajtovi: {val_key_bytes.hex()} (očekivano 55aa)."

            # 4. Skeniranje sekcija kataloga u potrazi za UEFI Platform ID 0xEF
            found_uefi = False
            total_entries = len(catalog_data) // 32

            for i in range(2, total_entries):
                entry = catalog_data[i * 32 : (i + 1) * 32]
                header_indicator = entry[0]
                platform_id = entry[1]

                # Section Header ima indikator 0x90 (nastavak) ili 0x91 (poslednja sekcija)
                if header_indicator in (0x90, 0x91):
                    if platform_id == 0xEF:
                        found_uefi = True
                        break

            if not found_uefi:
                return False, "Nedostaje UEFI El Torito zapis (Platform ID 0xEF)!"

            # 5. Skeniranje za UDF Volume Recognition Sequence (ECMA-167: BEA01 + NSR02/NSR03)
            # Neophodno za iVentoy PXE mrežni boot i standardni Windows installer
            found_udf = False
            for sec_idx in range(16, 64):
                f.seek(sec_idx * SECTOR_SIZE)
                sec = f.read(SECTOR_SIZE)
                if len(sec) < SECTOR_SIZE:
                    break
                if sec[0] == 0xFF:  # ISO Volume Descriptor Set Terminator
                    b1 = f.read(SECTOR_SIZE)
                    b2 = f.read(SECTOR_SIZE)
                    if len(b1) >= 6 and len(b2) >= 6:
                        if b1[1:6] == b"BEA01" and b2[1:6] in (b"NSR02", b"NSR03"):
                            found_udf = True
                    break

            if found_udf:
                return True, "Potvrđeni UEFI (0xEF), BIOS (0x00) boot unosi i UDF ECMA-167 fajlsistem (iVentoy PXE & Ventoy spremno)"
            else:
                return True, "Potvrđeni UEFI (0xEF) i BIOS (0x00) boot unosi (upozorenje: UDF nije detektovan, iVentoy PXE može tretirati ISO kao WinPE)"

    except Exception as e:
        return False, f"Greška pri parsiranju El Torito boot kataloga: {e}"


def calculate_hashes(file_path: Path, progress_cb: Optional[Callable[[float], None]] = None) -> Tuple[str, str]:
    """Izračunava MD5 i SHA-256 heš za zadati fajl."""
    md5_hash = hashlib.md5()
    sha256_hash = hashlib.sha256()

    total_size = file_path.stat().st_size
    read_bytes = 0
    chunk_size = 4 * 1024 * 1024  # 4 MB

    with open(file_path, "rb") as f:
        while True:
            chunk = f.read(chunk_size)
            if not chunk:
                break
            md5_hash.update(chunk)
            sha256_hash.update(chunk)
            read_bytes += len(chunk)
            if progress_cb and total_size > 0:
                progress_cb(min(1.0, read_bytes / total_size))

    return md5_hash.hexdigest(), sha256_hash.hexdigest()


class IsoBuilder:
    """
    Glavna klasa za pripremu staging stabla i generisanje Ventoy Windows 11 ISO slike.
    """

    def __init__(
        self,
        project_root: Optional[Path] = None,
        output_path: Optional[Path] = None,
        profile: str = "lean",
        image_format: str = "auto",
        no_prompt: bool = True,
        force: bool = False,
        dry_run: bool = False,
        preview: bool = False,
        engine: str = "auto",
        staging_dir: Optional[Path] = None,
        skip_media_verify: bool = False,
        **kwargs
    ):
        self.project_root = Path(project_root or PROJECT_ROOT).resolve()
        self.profile = (profile or "lean").strip().lower()
        if self.profile not in ["lean", "full"]:
            raise ValueError(f"Nepodržan profil '{profile}'. Dozvoljeni profili su 'lean' i 'full'.")
        self.image_format = (image_format or "auto").strip().lower()
        if self.image_format not in ["auto", "esd", "wim"]:
            raise ValueError(f"Nepodržan format slike '{image_format}'. Dozvoljeni formati su 'auto', 'esd' i 'wim'.")
        self.no_prompt = no_prompt
        self.force = force
        self.dry_run = dry_run
        self.preview = preview
        self.skip_media_verify = skip_media_verify
        self.engine_name = engine
        self.custom_staging_dir = Path(staging_dir).resolve() if staging_dir else None

        # Određivanje finalne izlazne putanje
        if output_path is None:
            self.output_path = get_default_output_path(self.project_root, profile=self.profile)
        else:
            p = Path(output_path).expanduser().resolve()
            if p.is_dir() or str(p).endswith("/") or not str(p).endswith(".iso"):
                target_build = read_target_build(self.project_root)
                suffix = "-Lean" if self.profile == "lean" else ""
                self.output_path = p / f"WIS-Win11-Pro-{target_build}{suffix}.iso"
            else:
                self.output_path = p

    def resolve_image_file(self) -> str:
        """
        Određuje koja slika sistema se koristi ('install.esd' ili 'install.wim')
        na osnovu self.image_format parametra ('auto', 'esd', 'wim').
        Vraća naziv fajla ('install.esd' ili 'install.wim').
        """
        esd_path = self.project_root / "sources" / "install.esd"
        wim_path = self.project_root / "sources" / "install.wim"

        fmt = self.image_format.lower()
        if fmt == "esd":
            if not esd_path.exists():
                raise FileNotFoundError(f"Izabran je format 'esd', ali datoteka ne postoji: {esd_path}")
            return "install.esd"
        elif fmt == "wim":
            if not wim_path.exists():
                raise FileNotFoundError(f"Izabran je format 'wim', ali datoteka ne postoji: {wim_path}")
            return "install.wim"
        elif fmt == "auto":
            if esd_path.exists():
                return "install.esd"
            elif wim_path.exists():
                return "install.wim"
            else:
                raise FileNotFoundError(
                    f"Nije pronađena Windows slika sistema u sources/ ('install.esd' ili 'install.wim')"
                )
        else:
            raise ValueError(
                f"Nepodržan format slike: '{self.image_format}'. Dozvoljeni su: 'auto', 'esd', 'wim'."
            )

    def _determine_staging_base(self) -> Path:
        """
        Određuje lokaciju privremenog staging direktorijuma na ISTOM fajlsistemu
        kako bi 'os.link' prošao u mikrosekundi bez trošenja prostora i RAM-a.
        """
        if self.custom_staging_dir:
            return self.custom_staging_dir

        # Proveravamo da li je /tmp na istom uređaju (mount-u)
        sys_temp = Path(tempfile.gettempdir())
        try:
            if sys_temp.stat().st_dev == self.project_root.stat().st_dev:
                return sys_temp / f"wis_iso_staging_{os.getpid()}_{int(time.time())}"
        except Exception:
            pass

        # Ako je /tmp na tmpfs ili drugom mountu, staging stavljamo u skriveni folder repozitorijuma
        staging_base = self.project_root / ".staging_iso_tmp"
        staging_base.mkdir(parents=True, exist_ok=True)
        return staging_base / f"staging_{os.getpid()}_{int(time.time())}"

    def validate_sources(self) -> List[str]:
        """Proverava postojanje obaveznih izvornih komponenti."""
        errors = []
        wim_path = self.project_root / "sources" / "install.wim"
        esd_path = self.project_root / "sources" / "install.esd"
        fmt = self.image_format.lower()

        if fmt == "esd":
            if not esd_path.exists():
                errors.append(f"Zahtevan je 'esd' format slike sistema, ali datoteka ne postoji: '{esd_path}'")
        elif fmt == "wim":
            if not wim_path.exists():
                errors.append(f"Zahtevan je 'wim' format slike sistema, ali datoteka ne postoji: '{wim_path}'")
        elif fmt == "auto":
            if not esd_path.exists() and not wim_path.exists():
                errors.append(f"Nedostaje Windows slika sistema u sources/: '{esd_path}' ili '{wim_path}'")
        else:
            errors.append(f"Nepoznat format slike: '{self.image_format}'. Dozvoljeni su: 'auto', 'esd', 'wim'.")

        boot_wim_path = self.project_root / "sources" / "boot.wim"
        if not boot_wim_path.exists():
            errors.append(f"Nedostaje WinPE bootloader slika: '{boot_wim_path}'")

        oem_dir = self.project_root / "sources" / "$OEM$"
        if not oem_dir.exists() or not oem_dir.is_dir():
            errors.append(f"Nedostaje obavezni direktorijum: '{oem_dir}'")

        boot_etfs = self.project_root / "boot" / "etfsboot.com"
        if not boot_etfs.exists():
            errors.append(f"Nedostaje BIOS bootloader: '{boot_etfs}'")

        efi_noprompt = self.project_root / "efi" / "microsoft" / "boot" / "efisys_noprompt.bin"
        efi_standard = self.project_root / "efi" / "microsoft" / "boot" / "efisys.bin"
        if not efi_noprompt.exists() and not efi_standard.exists():
            errors.append("Nedostaje UEFI bootloader: 'efisys_noprompt.bin' ili 'efisys.bin'")

        required_boot_files = ["autounattend.xml", "FirstLogon.cmd", "WIS-AcceleratePE.cmd"]
        if not self.preview:
            required_boot_files.append("Wifi-Servis.ini")
        for req_file in required_boot_files:
            p = self.project_root / req_file
            if not p.exists():
                errors.append(f"Nedostaje ključna skripta/konfiguracija: '{p}'")

        # Provera VMD skladišnih drajvera (striktno očuvanje)
        vmd_dir = self.project_root / "Za disk 10+ gen"
        if not vmd_dir.exists() or not vmd_dir.is_dir():
            errors.append(f"Nedostaje obavezni direktorijum Intel VMD drajvera: '{vmd_dir}'")

        # Provera mrežnih i skladišnih drajvera u OEM stablu
        oem_drivers = self.project_root / "sources" / "$OEM$" / "$1" / "setup" / "Drivers"
        if not oem_drivers.exists() or not oem_drivers.is_dir():
            errors.append(f"Nedostaje direktorijum drajvera u OEM stablu: '{oem_drivers}'")

        # Za 'full' profil Programi.tar mora postojati na disku
        if self.profile == "full":
            programi_tar = self.project_root / "Programi.tar"
            if not programi_tar.exists():
                errors.append(f"Za 'full' profil je neophodna arhivska datoteka: '{programi_tar}'")

        return errors

    def populate_staging(self, staging_dir: Path, log_cb: Optional[Callable[[str], None]] = None) -> Dict[str, int]:
        """
        Popunjava staging stablo hard linkovima (os.link) sa fallbackom na copy2.
        Primenjuje filtere i uklanja bootfix.bin ako je no_prompt aktivan.
        U 'lean' profilu isključuje Programi.tar radi uštede ~4.45 GB.
        """
        stats = {"linked": 0, "copied": 0, "skipped": 0}

        def _log(msg: str):
            if log_cb:
                log_cb(msg)

        _log(f"Kreiram staging stablo u: {staging_dir}")
        staging_dir.mkdir(parents=True, exist_ok=True)

        # Određivanje aktivne slike sistema
        selected_image = self.resolve_image_file()
        _log(f"  [+] Odabrana slika sistema: 'sources/{selected_image}' (režim: {self.image_format})")

        root_items = INCLUDED_ROOT_ITEMS + (["WIS-PREVIEW-TEST-ONLY.json", "WIS-PREVIEW-README.txt"] if self.preview else [])
        for item_name in root_items:
            # U 'lean' profilu Programi.tar se striktno ISKLJUČUJE iz korena ISO-a
            if self.profile == "lean" and item_name.lower() == "programi.tar":
                stats["skipped"] += 1
                _log("  [-] Lean profil aktivan: preskačem 'Programi.tar' u korenu ISO-a (ušteda 4.45 GB)")
                continue

            src_path = self.project_root / item_name
            if item_name == "version.json" and not src_path.exists():
                src_path = self.project_root.parent / item_name
            if not src_path.exists():
                continue

            dst_path = staging_dir / item_name

            if src_path.is_file():
                try:
                    os.link(src_path, dst_path)
                    stats["linked"] += 1
                except Exception:
                    shutil.copy2(src_path, dst_path)
                    stats["copied"] += 1

            elif src_path.is_dir():
                for root, dirs, files in os.walk(src_path):
                    posix_root = Path(root).as_posix()
                    is_oem = "$OEM$" in posix_root or "$1" in posix_root

                    # Filtriranje direktorijuma na licu mesta:
                    # 'Programi' se izuzima SAMO ako NIJE unutar '$OEM$' ili 'sources' stabla
                    # jer Windows Setup tokom WinPE faze automatski kopira sources/$OEM$/$1/ na sistemski disk C:\
                    dirs[:] = [d for d in dirs if (is_oem or d not in EXCLUDED_NAMES) and not d.startswith(".")]

                    rel_root = Path(root).relative_to(self.project_root)
                    target_dir = staging_dir / rel_root
                    target_dir.mkdir(parents=True, exist_ok=True)

                    for file_name in files:
                        lower_name = file_name.lower()

                        # U 'lean' profilu Programi.tar se striktno ISKLJUČUJE i unutar sources/$OEM$/... ili bilo kog podfoldera
                        if self.profile == "lean" and lower_name == "programi.tar":
                            stats["skipped"] += 1
                            _log(f"  [-] Lean profil aktivan: preskačem '{rel_root / file_name}' (ušteda prostora)")
                            continue

                        # Selekcija slike sistema u sources/ folderu:
                        # U staging se kopira/linkuje SAMO izabrana slika (install.esd ili install.wim).
                        # Ako se koristi install.esd, install.wim se striktno ignoriše!
                        if rel_root.as_posix() == "sources":
                            if lower_name in ("install.wim", "install.esd"):
                                if lower_name != selected_image.lower():
                                    stats["skipped"] += 1
                                    _log(f"  [-] Preskačem '{rel_root / file_name}' (aktivna slika je '{selected_image}')")
                                    continue

                        # Svi *.bak* fajlovi i temp fajlovi se STRIKTNO IGNORIŠU
                        if (
                            (file_name in EXCLUDED_NAMES and not is_oem)
                            or file_name.startswith(".")
                            or ".bak" in lower_name
                            or lower_name.endswith(".tmp")
                        ):
                            stats["skipped"] += 1
                            continue

                        # Pravilo za no-prompt: Ukloniti boot/bootfix.bin
                        if self.no_prompt and file_name.lower() == "bootfix.bin":
                            rel_file_path = (rel_root / file_name).as_posix().lower()
                            if "boot/bootfix.bin" in rel_file_path:
                                stats["skipped"] += 1
                                _log("  [-] no-prompt mod: preskačem 'boot/bootfix.bin'")
                                continue

                        src_file = Path(root) / file_name
                        dst_file = target_dir / file_name

                        try:
                            os.link(src_file, dst_file)
                            stats["linked"] += 1
                        except Exception:
                            shutil.copy2(src_file, dst_file)
                            stats["copied"] += 1

        _log(f"Staging stablo završeno: {stats['linked']} hard linkova, {stats['copied']} kopiranih, {stats['skipped']} preskočenih.")
        return stats

    def build_iso(
        self,
        progress_cb: Optional[Callable[[Dict[str, Any]], None]] = None,
        log_cb: Optional[Callable[[str], None]] = None
    ) -> Dict[str, Any]:
        """
        Glavni izvršni metod za generisanje ISO slike.
        """
        start_time = time.time()
        if not self.dry_run and not self.skip_media_verify:
            # Ne sklapa novu verziju iz starih OEM/bin kopija sa istim brojem verzije.
            import importlib.util
            spec = importlib.util.spec_from_file_location("wis_media", SCRIPT_DIR / "Prepare-WISMedia.py")
            media = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(media)
            if self.preview:
                if "PREVIEW" not in self.output_path.name or self.profile != "lean":
                    raise ValueError("TEST ISO zahteva PREVIEW u imenu i lean profil.")
                preview_spec = importlib.util.spec_from_file_location("wis_preview", SCRIPT_DIR / "Build-PreviewMedia.py")
                preview_module = importlib.util.module_from_spec(preview_spec)
                preview_spec.loader.exec_module(preview_module)
                preview_module.verify_preview(self.project_root)
            else:
                media.verify_media(self.project_root)

        def _log(msg: str):
            if log_cb:
                log_cb(msg)
            else:
                print(msg)

        def _update(step: str, pct: int, status: str = "running"):
            if progress_cb:
                progress_cb({
                    "step": step,
                    "progress": pct,
                    "status": status,
                    "output_path": str(self.output_path)
                })

        _update("Validacija izvornih fajlova", 5)
        _log("=" * 80)
        _log("        WIS - GENERATOR VENTOY BOOTABLE WINDOWS 11 ISO SLIKE")
        _log("=" * 80)
        _log(f"📍 Koren repozitorijuma:  {self.project_root}")
        _log(f"📦 Izabrani profil:       {self.profile.upper()} ({'Lean Network ISO bez Programi.tar (~6-8 GB)' if self.profile == 'lean' else 'Full Offline ISO sa Programi.tar (~11-13 GB)'})")
        _log(f"🎯 Izlazna ISO datoteka:  {self.output_path}")
        _log(f"⚡ No-Prompt butovanje:   {'DA (direktan start bez Press any key)' if self.no_prompt else 'NE (standardni prompt)'}")
        _log(f"🖼️  Format slike sistema:   {self.image_format.upper()} (režim selekcije)")
        _log(f"⚙️  Izabrani motor:        {self.engine_name}")
        _log(f"🧪 Dry-Run simulacija:    {'DA' if self.dry_run else 'NE'}")
        _log("-" * 80)

        # 1. Validacija izvora
        errors = self.validate_sources()
        if errors:
            err_msg = "Detektovane greške u strukturi repozitorijuma:\n" + "\n".join(f" - {e}" for e in errors)
            _update("Greška pri validaciji", 0, "error")
            raise RuntimeError(err_msg)

        selected_image = self.resolve_image_file()
        _log(f"ℹ️  Aktivna slika sistema:  sources/{selected_image}")

        # 2. Provera slobodnog prostora
        _update("Provera slobodnog prostora na disku", 10)
        if self.profile == "lean":
            min_required_gb = 6.5 if selected_image == "install.esd" else 9.0
        else:
            min_required_gb = 11.5 if selected_image == "install.esd" else 14.0

        ok_space, free_gb, total_gb = check_disk_space(self.output_path, min_gb=min_required_gb)
        _log(f"💾 Prostor na ciljnoj particiji: {free_gb:.2f} GB slobodno od {total_gb:.2f} GB ukupno (zahtevano min. {min_required_gb:.1f} GB za {self.profile} profil sa {selected_image}).")
        if not ok_space and not self.dry_run:
            err_msg = (
                f"Nedovoljno slobodnog prostora na ciljnoj particiji ({self.output_path.parent})!\n"
                f"Dostupno je {free_gb:.2f} GB, a potrebno je minimum {min_required_gb:.1f} GB za stabilnu izradu ISO slike ({self.profile} profil)."
            )
            _update("Nedovoljno prostora na disku", 10, "error")
            raise RuntimeError(err_msg)

        # 3. Provera postojećeg fajla
        if self.output_path.exists():
            if not self.force and not self.dry_run:
                err_msg = (
                    f"Ciljni ISO fajl već postoji: {self.output_path}\n"
                    f"Koristite parametar --force za automatsko prepisivanje."
                )
                _update("Fajl već postoji", 10, "error")
                raise FileExistsError(err_msg)
            elif self.force:
                _log(f"[UPOZORENJE] Fajl već postoji i biće prepisan (--force): {self.output_path}")

        # 4. Detekcija ISO engine-a
        _update("Detekcija ISO motora", 15)
        engine_type, engine_bin = detect_iso_engine(self.engine_name)
        _log(f"🛠️  Detektovan ISO generator: [{engine_type.upper()}] -> {engine_bin}")

        # Određivanje odgovarajućeg EFI boot fajla
        efi_rel_path = "efi/microsoft/boot/efisys.bin"
        if self.no_prompt:
            efi_noprompt_file = self.project_root / "efi" / "microsoft" / "boot" / "efisys_noprompt.bin"
            if efi_noprompt_file.exists():
                efi_rel_path = "efi/microsoft/boot/efisys_noprompt.bin"

        staging_dir = self._determine_staging_base()

        # Konstrukcija komande za genisoimage / mkisofs (UDF 1.02/2.00 + UEFI 0xEF za iVentoy PXE i standardni Windows Setup)
        genisoimage_base_args = [
            "-udf",
            "-iso-level", "4",
            "-allow-limited-size",
            "-r",
            "-l",
            "-D",
            "-J", "-joliet-long",
            "-b", "boot/etfsboot.com",
            "-no-emul-boot",
            "-boot-load-size", "8",
            "-eltorito-alt-boot",
            "-e", efi_rel_path,
            "-no-emul-boot",
            "-V", "WIS_WIN11",
            "-o", str(self.output_path),
            str(staging_dir)
        ]

        # Konstrukcija komande za xorriso
        xorriso_base_args = [
            "-as", "mkisofs",
            "-iso-level", "4",
            "-rock",
            "-disable-deep-relocation",
            "-untranslated-filenames",
            "-J", "-joliet-long",
            "-b", "boot/etfsboot.com",
            "-no-emul-boot",
            "-boot-load-size", "8",
            "-eltorito-alt-boot",
            "-eltorito-platform", "efi",
            "-b", efi_rel_path,
            "-no-emul-boot",
            "-V", "WIS_WIN11",
            "-o", str(self.output_path),
            str(staging_dir)
        ]

        cmd: List[str] = []
        if engine_type == "host_genisoimage":
            cmd = [engine_bin] + genisoimage_base_args
        elif engine_type == "host_xorriso":
            cmd = [engine_bin] + xorriso_base_args
        elif engine_type == "docker_xorriso":
            if not self.dry_run:
                ensure_docker_image(docker_bin=engine_bin, log_cb=_log)
            else:
                _log(f"🐳 Docker xorriso mod: koristiće se slika '{DOCKER_IMAGE_NAME}' preko {engine_bin}")

            uid = os.getuid() if hasattr(os, "getuid") else 1000
            gid = os.getgid() if hasattr(os, "getgid") else 1000

            s_dir = str(staging_dir.resolve())
            o_dir = str(self.output_path.parent.resolve())

            mount_args = ["-v", f"{s_dir}:{s_dir}"]
            if o_dir != s_dir and not s_dir.startswith(o_dir + "/"):
                mount_args.extend(["-v", f"{o_dir}:{o_dir}"])
            elif s_dir.startswith(o_dir + "/"):
                mount_args = ["-v", f"{o_dir}:{o_dir}"]

            cmd = [
                engine_bin, "run", "--rm",
                "-u", f"{uid}:{gid}",
            ] + mount_args + [
                DOCKER_IMAGE_NAME,
                "xorriso"
            ] + xorriso_base_args
        else:
            raise RuntimeError(f"Nepodržan tip motora u fazi izgradnje: {engine_type}")

        # Ako je DRY-RUN, simuliramo i završavamo odmah
        if self.dry_run:
            _log("-" * 80)
            _log("🧪 [DRY-RUN] Simulacija je uspešna! Nisu napravljene izmene na disku.")
            _log(f"   Planirana komanda motora:\n   {' '.join(cmd)}")
            _log(f"   Planirano staging stablo bi bilo u: {staging_dir}")
            _log("-" * 80)
            _update("Dry-Run simulacija uspešna", 100, "done")
            return {
                "success": True,
                "dry_run": True,
                "profile": self.profile,
                "image_format": self.image_format,
                "selected_image": selected_image,
                "output_path": str(self.output_path),
                "engine": engine_type,
                "command": " ".join(cmd),
                "duration": round(time.time() - start_time, 2),
                "no_prompt": self.no_prompt
            }

        # 5. Formiranje Staging Stabla
        _update("Kreiranje staging stabla sa hard linkovima", 20)
        try:
            self.populate_staging(staging_dir, log_cb=_log)

            # Osiguravamo postojanje roditeljskog direktorijuma ciljnog ISO fajla
            self.output_path.parent.mkdir(parents=True, exist_ok=True)

            # 6. Pokretanje ISO motora
            _update("Generisanje ISO slike (može potrajati par minuta)...", 40)
            _log(f"🚀 Pokrećem ISO motor: {' '.join(cmd)}")

            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1
            )

            if proc.stdout:
                for line in proc.stdout:
                    line_clean = line.strip()
                    if line_clean:
                        _log(f"  [{engine_type}] {line_clean}")

            proc.wait()
            if proc.returncode != 0:
                raise subprocess.CalledProcessError(proc.returncode, cmd)

            # 7. Provera generisanog fajla
            if not self.output_path.exists():
                raise FileNotFoundError(f"ISO fajl nije kreiran na očekivanoj putanji: {self.output_path}")

            iso_size_bytes = self.output_path.stat().st_size
            iso_size_gb = iso_size_bytes / (1024 ** 3)
            iso_size_mb = iso_size_bytes / (1024 ** 2)

            _log("-" * 80)
            _log(f"✅ ISO slika je generisana na disku!")
            _log(f"   Veličina: {iso_size_gb:.2f} GB ({iso_size_mb:.1f} MB, {iso_size_bytes:,} bajtova)")

            # 7.1 Revizija El Torito Boot Kataloga (UEFI 0xEF i BIOS 0x00)
            _update("Revizija El Torito boot kataloga...", 80)
            _log("🛡️  Sprovodim inspekciju El Torito boot kataloga (UEFI/BIOS)...")
            audit_ok, audit_msg = audit_boot_catalog(self.output_path)
            if not audit_ok:
                _log(f"❌ [KRITIČNA GREŠKA] Inspekcija butabilnosti nije uspela: {audit_msg}")
                try:
                    self.output_path.unlink(missing_ok=True)
                    _log(f"🗑️  Obrisan defektni ISO fajl: {self.output_path}")
                except Exception as del_err:
                    _log(f"⚠️  Neuspešno brisanje defektnog ISO-a: {del_err}")
                _update("Neuspela revizija boot kataloga", 80, "error")
                raise RuntimeError(f"Defektna ISO slika odbijena: {audit_msg}")

            _log(f"✅ {audit_msg}")

            # 8. Računanje kontrolnih suma
            _update("Izračunavanje heš vrednosti (MD5 i SHA-256)...", 85)
            _log("🔍 Računam MD5 i SHA-256 kontrolne sume...")

            md5_val, sha256_val = calculate_hashes(
                self.output_path,
                progress_cb=lambda r: _update("Izračunavanje kontrolne sume...", int(85 + r * 14))
            )

            total_duration = time.time() - start_time
            _log("-" * 80)
            _log("📋 DETALJAN IZVEŠTAJ GENERISANE ISO SLIKE:")
            _log(f"   📁 Datoteka: {self.output_path}")
            _log(f"   📦 Profil:   {self.profile.upper()}")
            _log(f"   📦 Veličina: {iso_size_gb:.2f} GB ({iso_size_bytes:,} bajtova)")
            _log(f"   ⏱️  Trajanje: {total_duration:.1f} sekundi ({total_duration/60:.2f} min)")
            _log(f"   🔑 MD5:     {md5_val}")
            _log(f"   🛡️  SHA-256: {sha256_val}")
            _log(f"   🚀 Ventoy:  Samo kopirajte ovaj ISO na Ventoy USB fleš disk!")
            _log("=" * 80)

            _update("ISO izgradnja uspešno završena", 100, "done")

            return {
                "success": True,
                "dry_run": False,
                "profile": self.profile,
                "image_format": self.image_format,
                "selected_image": selected_image,
                "output_path": str(self.output_path),
                "iso_size_bytes": iso_size_bytes,
                "iso_size_gb": round(iso_size_gb, 2),
                "iso_size_mb": round(iso_size_mb, 1),
                "duration": round(total_duration, 1),
                "md5": md5_val,
                "sha256": sha256_val,
                "engine": engine_type,
                "no_prompt": self.no_prompt
            }

        finally:
            # 9. Brisanje privremenog staging direktorijuma
            if staging_dir.exists():
                _log(f"🧹 Uklanjam privremeni staging folder: {staging_dir}")
                shutil.rmtree(staging_dir, ignore_errors=True)

    def build(
        self,
        progress_cb: Optional[Callable[[Dict[str, Any]], None]] = None,
        log_cb: Optional[Callable[[str], None]] = None
    ) -> Dict[str, Any]:
        """Sinonim za build_iso()."""
        return self.build_iso(progress_cb=progress_cb, log_cb=log_cb)


def parse_arguments():
    """Parsira argumente komandne linije."""
    parser = argparse.ArgumentParser(
        description="WIS - Generator Ventoy Bootable Windows 11 ISO Slike",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Primeri korišćenja:
  python3 Skripte/Build-Iso.py --dry-run
  python3 Skripte/Build-Iso.py --profile lean --dry-run
  python3 Skripte/Build-Iso.py --profile full --dry-run
  python3 Skripte/Build-Iso.py --output ~/Downloads/MojaSlika.iso --force
  python3 Skripte/Build-Iso.py --engine auto
  ./napravi-iso.sh --profile lean
  ./napravi-iso.sh --profile full
        """
    )

    parser.add_argument(
        "--profile", "-p",
        choices=["lean", "full"],
        default="lean",
        help="Izbor profila izgradnje ISO slike: 'lean' (podrazumevano, Lean Network ISO bez Programi.tar za radionicu i iVentoy PXE, ~8 GB) ili 'full' (Full Offline ISO sa Programi.tar za terenske USB-ove, ~13 GB)"
    )

    parser.add_argument(
        "--output", "-o",
        type=str,
        default=None,
        help="Putanja do izlaznog ISO fajla ili direktorijuma gde se snima (default: ~/Downloads)"
    )

    parser.add_argument(
        "--no-prompt",
        dest="no_prompt",
        action="store_true",
        default=True,
        help="Uklanja 'Press any key...' prompt za automatsko butovanje (default: True)"
    )

    parser.add_argument(
        "--prompt",
        dest="no_prompt",
        action="store_false",
        help="Zadržava standardni 'Press any key...' prompt"
    )

    parser.add_argument(
        "--force", "-f",
        action="store_true",
        help="Automatski prepiši postojeći ISO fajl bez potvrde"
    )

    parser.add_argument("--preview", action="store_true", help="Isključivo potpisan TEST preview medijum; zahteva PREVIEW naziv ISO-a")

    parser.add_argument(
        "--dry-run", "-n",
        action="store_true",
        help="Simulira proces izgradnje bez kreiranja staging-a i pokretanja ISO motora"
    )

    parser.add_argument(
        "--engine", "-e",
        choices=["auto", "genisoimage", "udf", "host", "docker", "xorriso", "mkisofs"],
        default="auto",
        help="Izbor alata za pravljenje ISO slike: 'auto' (genisoimage UDF primarno, xorriso fallback), 'genisoimage'/'udf', 'host'/'xorriso', 'docker' (default: auto)"
    )

    parser.add_argument(
        "--image-format",
        choices=["auto", "esd", "wim"],
        default="auto",
        help="Format Windows instalacione slike sistema u sources/: 'auto' (podrazumevano, koristi install.esd ako postoji, inače install.wim), 'esd' (zahteva install.esd) ili 'wim' (koristi install.wim)"
    )

    parser.add_argument(
        "--skip-media-verify",
        action="store_true",
        help="Preskače proveru .wis-media-attestation.json za lokalne i direktne radioničke build-ove"
    )

    parser.add_argument(
        "--staging-dir",
        type=str,
        default=None,
        help="Eksplicitna putanja za privremeni staging direktorijum"
    )

    return parser.parse_args()


def main():
    args = parse_arguments()

    builder = IsoBuilder(
        project_root=PROJECT_ROOT,
        output_path=args.output,
        profile=args.profile,
        image_format=args.image_format,
        no_prompt=args.no_prompt,
        force=args.force,
        dry_run=args.dry_run,
        preview=args.preview,
        engine=args.engine,
        staging_dir=args.staging_dir,
        skip_media_verify=args.skip_media_verify
    )

    try:
        result = builder.build_iso()
        return 0 if result.get("success") else 1
    except Exception as err:
        print(f"\n[FATALNA GRESKA] Izgradnja ISO slike nije uspela: {err}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
