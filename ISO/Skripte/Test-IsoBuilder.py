#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
================================================================================
GWADT QA SUITE - Sveobuhvatna verifikacija Ventoy Windows 11 ISO Generatora
================================================================================
Skripte/Test-IsoBuilder.py

Ovaj test suite sprovodi Dev <-> QA zatvorenu petlju za proveru:
1. Detekcije verzije i formiranja naziva ISO slike (version.json)
2. CLI argumenata i `--dry-run` simulacije (Skripte/Build-Iso.py)
3. Bash wrappera na root nivou (napravi-iso.sh, izvršnost i prosleđivanje argumenata)
4. Filter mehanizma i hard-link staging stabla (uključivanje obaveznih, isključivanje zabranjenih)
5. No-Prompt logike (izostavljanje bootfix.bin, odabir efisys_noprompt.bin)
6. Validacije izvornih fajlova i provere prostora na disku
7. Asinhronog menadžera (USB Tool/core/iso_builder.py - stanje, lifecycle, niti)
8. REST API ruta u USB Tool backendu (GET /api/build-iso/status, POST /api/build-iso)
9. Integriteta Web GUI datoteka (index.html, app.js, style.css)
================================================================================
"""

import os
import sys
import json
import time
import shutil
import tempfile
import threading
import subprocess
from pathlib import Path
from typing import List, Dict, Any, Tuple
from urllib.request import Request, urlopen
from urllib.error import HTTPError

# Određivanje putanja
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_ROOT = SCRIPT_DIR.parent
BUILD_ISO_SCRIPT = SCRIPT_DIR / "Build-Iso.py"
BASH_WRAPPER = PROJECT_ROOT / "napravi-iso.sh"
USB_TOOL_DIR = PROJECT_ROOT / "USB Tool"

# Uvoz modula za testiranje
sys.path.insert(0, str(SCRIPT_DIR))
sys.path.insert(0, str(USB_TOOL_DIR))

try:
    import importlib.util
    spec = importlib.util.spec_from_file_location("build_iso_module", str(BUILD_ISO_SCRIPT))
    build_iso_mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(build_iso_mod)
    
    IsoBuilder = getattr(build_iso_mod, "IsoBuilder")
    read_target_build = getattr(build_iso_mod, "read_target_build")
    get_default_output_path = getattr(build_iso_mod, "get_default_output_path")
    check_disk_space = getattr(build_iso_mod, "check_disk_space")
    detect_iso_engine = getattr(build_iso_mod, "detect_iso_engine")
    audit_boot_catalog = getattr(build_iso_mod, "audit_boot_catalog")
    INCLUDED_ROOT_ITEMS = getattr(build_iso_mod, "INCLUDED_ROOT_ITEMS")
    EXCLUDED_NAMES = getattr(build_iso_mod, "EXCLUDED_NAMES")
except Exception as e:
    print(f"[FATALNA GRESKA] Uvoz Build-Iso.py nije uspeo: {e}", file=sys.stderr)
    sys.exit(1)

try:
    from core.iso_builder import IsoBuildManager
    from app import UsbToolRequestHandler
    from http.server import ThreadingHTTPServer
except Exception as e:
    print(f"[FATALNA GRESKA] Uvoz USB Tool komponenti nije uspeo: {e}", file=sys.stderr)
    sys.exit(1)


class TestResultTracker:
    """Evidencija i formatiran prikaz rezultata svih testova."""

    def __init__(self):
        self.results: List[Dict[str, Any]] = []

    def record(self, category: str, test_name: str, passed: bool, details: str = ""):
        self.results.append({
            "category": category,
            "test_name": test_name,
            "passed": passed,
            "details": details
        })
        status_tag = "[\033[92mPASS\033[0m]" if passed else "[\033[91mFAIL\033[0m]"
        detail_txt = f" - {details}" if details else ""
        print(f" {status_tag} {test_name:<48}{detail_txt}")

    def all_passed(self) -> bool:
        return all(r["passed"] for r in self.results)

    def print_summary_table(self):
        print("\n" + "=" * 80)
        print("                  GWADT QA TEST SUITE - ZBIRNI IZVESTAJ")
        print("=" * 80)
        print(f"{'Kategorija':<24} | {'Test Slučaj':<40} | {'Status'}")
        print("-" * 80)
        for r in self.results:
            status_str = "PASS" if r["passed"] else "FAIL"
            print(f"{r['category']:<24} | {r['test_name']:<40} | {status_str}")
        print("-" * 80)

        total = len(self.results)
        passed = sum(1 for r in self.results if r["passed"])
        failed = total - passed
        pct = (passed / total * 100) if total > 0 else 0

        print(f"Ukupno testova: {total} | Položeno: {passed} | Palo: {failed} | Uspešnost: {pct:.1f}%")
        if failed == 0:
            print("\033[92m[ZAKLJUCAK] SVE KOMPONENTE VENTOY ISO GENERATORA SU 100% ISPRAVNE!\033[0m")
        else:
            print("\033[91m[ZAKLJUCAK] DETEKTOVANE SU GREŠKE U QA VERIFIKACIJI!\033[0m")
        print("=" * 80 + "\n")


def test_section_version_detection(tracker: TestResultTracker):
    """TEST 1: Detekcija verzije i izlazne putanje."""
    print("\n[TEST 1] Verifikacija čitanja verzije i kreiranja ISO naziva...")

    # 1.1 Provera version.json na pravom projektu
    build_str = read_target_build(PROJECT_ROOT)
    expected_build = "10.0.26200.8037"
    tracker.record(
        "Detekcija Verzije",
        "Čitanje os_build_target iz version.json",
        build_str == expected_build,
        f"Očitano: {build_str} (Očekivano: {expected_build})"
    )

    # 1.2 Provera naziva default ISO fajla (Lean podrazumevano i Full profil)
    default_iso_lean = get_default_output_path(PROJECT_ROOT, profile="lean")
    expected_filename_lean = f"GWADT-Win11-Pro-{expected_build}-Lean.iso"
    default_iso_full = get_default_output_path(PROJECT_ROOT, profile="full")
    expected_filename_full = f"GWADT-Win11-Pro-{expected_build}.iso"
    tracker.record(
        "Detekcija Verzije",
        "Naziv generisanog ISO fajla (Lean profil)",
        default_iso_lean.name == expected_filename_lean,
        f"Ime fajla: {default_iso_lean.name}"
    )
    tracker.record(
        "Detekcija Verzije",
        "Naziv generisanog ISO fajla (Full profil)",
        default_iso_full.name == expected_filename_full,
        f"Ime fajla: {default_iso_full.name}"
    )

    # 1.3 Provera fallback mehanizma kada version.json ne postoji
    with tempfile.TemporaryDirectory() as tmp_dir:
        fallback_build = read_target_build(Path(tmp_dir))
        tracker.record(
            "Detekcija Verzije",
            "Fallback kada version.json ne postoji",
            fallback_build == expected_build,
            f"Vraćen fallback: {fallback_build}"
        )

    # 1.4 Provera GWADT_HOST_DOWNLOADS okruženja
    with tempfile.TemporaryDirectory() as tmp_downloads:
        os.environ["GWADT_HOST_DOWNLOADS"] = tmp_downloads
        try:
            custom_out = get_default_output_path(PROJECT_ROOT, profile="lean")
            tracker.record(
                "Detekcija Verzije",
                "Poštovanje GWADT_HOST_DOWNLOADS promenljive",
                custom_out.parent == Path(tmp_downloads) and custom_out.name == expected_filename_lean,
                f"Putanja: {custom_out}"
            )
        finally:
            os.environ.pop("GWADT_HOST_DOWNLOADS", None)


def test_section_cli_and_dry_run(tracker: TestResultTracker):
    """TEST 2: Provera CLI opcija i --dry-run rada."""
    print("\n[TEST 2] Provera CLI argumenata i --dry-run simulacije...")

    # 2.1 Direktno pokretanje Build-Iso.py --dry-run (Default Lean)
    proc = subprocess.run(
        [sys.executable, str(BUILD_ISO_SCRIPT), "--dry-run"],
        capture_output=True,
        text=True
    )
    is_success = (proc.returncode == 0)
    has_dry_marker = "[DRY-RUN]" in proc.stdout
    has_target_iso = "GWADT-Win11-Pro-10.0.26200.8037-Lean.iso" in proc.stdout
    has_lean_profile = "LEAN" in proc.stdout
    has_engine_detected = "Detektovan ISO generator" in proc.stdout

    tracker.record(
        "CLI Engine",
        "Izvršenje 'Build-Iso.py --dry-run' (Default Lean)",
        is_success and has_dry_marker and has_target_iso and has_lean_profile and has_engine_detected,
        f"Exit kod: {proc.returncode}, DryRun: {has_dry_marker}, Lean: {has_lean_profile}"
    )

    # 2.1b Direktno pokretanje Build-Iso.py --profile full --dry-run
    proc_full = subprocess.run(
        [sys.executable, str(BUILD_ISO_SCRIPT), "--profile", "full", "--dry-run"],
        capture_output=True,
        text=True
    )
    is_full_success = (proc_full.returncode == 0)
    has_full_iso = "GWADT-Win11-Pro-10.0.26200.8037.iso" in proc_full.stdout
    has_full_profile = "FULL" in proc_full.stdout

    tracker.record(
        "CLI Engine",
        "Izvršenje 'Build-Iso.py --profile full --dry-run'",
        is_full_success and has_full_iso and has_full_profile,
        f"Exit kod: {proc_full.returncode}, Full: {has_full_profile}"
    )

    # 2.2 Provera opcije --help
    proc_help = subprocess.run(
        [sys.executable, str(BUILD_ISO_SCRIPT), "--help"],
        capture_output=True,
        text=True
    )
    has_help_opts = (
        "--profile" in proc_help.stdout and
        "--dry-run" in proc_help.stdout and
        "--no-prompt" in proc_help.stdout and
        "--force" in proc_help.stdout and
        "--engine" in proc_help.stdout
    )
    tracker.record(
        "CLI Engine",
        "Podrška za sve CLI argumente (--help)",
        proc_help.returncode == 0 and has_help_opts,
        "Svi ključni parametri uključujući --profile prisutni u help meniju"
    )


def test_section_bash_wrapper(tracker: TestResultTracker):
    """TEST 3: Provera napravi-iso.sh root wrappera."""
    print("\n[TEST 3] Provera root Bash wrappera (napravi-iso.sh)...")

    # 3.1 Provera postojanja i izvršnih dozvola (+x)
    exists = BASH_WRAPPER.exists()
    is_executable = os.access(str(BASH_WRAPPER), os.X_OK)
    tracker.record(
        "Bash Wrapper",
        "Postojanje i izvršne dozvole (+x)",
        exists and is_executable,
        f"Postoji: {exists}, Izvršan (+x): {is_executable}"
    )

    # 3.2 Pokretanje napravi-iso.sh sa --dry-run
    proc = subprocess.run(
        [str(BASH_WRAPPER), "--dry-run"],
        cwd=str(PROJECT_ROOT),
        capture_output=True,
        text=True
    )
    passed = (proc.returncode == 0) and ("[DRY-RUN]" in proc.stdout)
    tracker.record(
        "Bash Wrapper",
        "Prosleđivanje --dry-run argumenta u Python",
        passed,
        f"Exit kod: {proc.returncode}"
    )

    # 3.3 Pokretanje napravi-iso.sh sa --help
    proc_help = subprocess.run(
        [str(BASH_WRAPPER), "--help"],
        cwd=str(PROJECT_ROOT),
        capture_output=True,
        text=True
    )
    tracker.record(
        "Bash Wrapper",
        "Prosleđivanje --help argumenta",
        proc_help.returncode == 0 and "GWADT - Generator Ventoy" in proc_help.stdout,
        "Help uspešno prosleđen i prikazan"
    )


def test_section_content_filters(tracker: TestResultTracker):
    """TEST 4: Verifikacija filtera sadržaja i staging stabla."""
    print("\n[TEST 4] Verifikacija filtera sadržaja i hard-link staging stabla...")

    with tempfile.TemporaryDirectory() as tmp_mock_root, tempfile.TemporaryDirectory() as tmp_staging:
        mock_root = Path(tmp_mock_root)
        staging_dir = Path(tmp_staging)

        # 4.1 Kreiranje mock obavezne strukture
        (mock_root / "sources" / "$OEM$" / "$$" / "setup").mkdir(parents=True, exist_ok=True)
        (mock_root / "sources" / "$OEM$" / "$$" / "setup" / "setup.cmd").write_text("echo test", encoding="utf-8")
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Programi").mkdir(parents=True, exist_ok=True)
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Programi" / "test_app.exe").write_text("dummy app", encoding="utf-8")
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Programi.tar").write_text("dummy oem tar", encoding="utf-8")
        (mock_root / "Programi.tar").write_text("dummy tar", encoding="utf-8")
        (mock_root / "sources" / "install.wim").write_text("dummy wim", encoding="utf-8")
        (mock_root / "sources" / "boot.wim").write_text("dummy bootwim", encoding="utf-8")
        (mock_root / "autounattend.xml").write_text("<xml/>", encoding="utf-8")
        (mock_root / "FirstLogon.cmd").write_text("dummy", encoding="utf-8")
        (mock_root / "GWADT-AcceleratePE.cmd").write_text("dummy accel", encoding="utf-8")
        (mock_root / "Wifi-Servis.ini").write_text("dummy", encoding="utf-8")
        (mock_root / "version.json").write_text('{"os_build_target": "10.0.26200.8037"}', encoding="utf-8")
        (mock_root / "Za disk 10+ gen").mkdir(parents=True, exist_ok=True)
        (mock_root / "Za disk 10+ gen" / "iaStorVD.inf").write_text("dummy inf", encoding="utf-8")
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Drivers").mkdir(parents=True, exist_ok=True)
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Drivers" / "net.inf").write_text("dummy net", encoding="utf-8")
        (mock_root / "boot").mkdir(parents=True, exist_ok=True)
        (mock_root / "boot" / "etfsboot.com").write_text("dummy etfs", encoding="utf-8")
        (mock_root / "boot" / "bootfix.bin").write_text("dummy bootfix", encoding="utf-8")
        (mock_root / "efi" / "microsoft" / "boot").mkdir(parents=True, exist_ok=True)
        (mock_root / "efi" / "microsoft" / "boot" / "efisys.bin").write_text("dummy efi", encoding="utf-8")
        (mock_root / "efi" / "microsoft" / "boot" / "efisys_noprompt.bin").write_text("dummy efi np", encoding="utf-8")
        (mock_root / "setup.exe").write_text("dummy setup", encoding="utf-8")

        # 4.2 Kreiranje zabranjenih / suvišnih elemenata
        (mock_root / "Zakrpe").mkdir(parents=True, exist_ok=True)
        (mock_root / "Zakrpe" / "windows11-kb.msu").write_text("dummy msu", encoding="utf-8")
        (mock_root / "Programi").mkdir(parents=True, exist_ok=True)
        (mock_root / "Programi" / "chrome.exe").write_text("dummy app", encoding="utf-8")
        (mock_root / ".git").mkdir(parents=True, exist_ok=True)
        (mock_root / ".git" / "HEAD").write_text("ref: refs/heads/main", encoding="utf-8")
        (mock_root / ".agents").mkdir(parents=True, exist_ok=True)
        (mock_root / ".agents" / "rules.md").write_text("rules", encoding="utf-8")
        (mock_root / "second_brain").mkdir(parents=True, exist_ok=True)
        (mock_root / "second_brain" / "note.md").write_text("notes", encoding="utf-8")
        (mock_root / "wiki").mkdir(parents=True, exist_ok=True)
        (mock_root / "wiki" / "overview.md").write_text("wiki", encoding="utf-8")
        (mock_root / "USB Tool").mkdir(parents=True, exist_ok=True)
        (mock_root / "USB Tool" / "app.py").write_text("usb app", encoding="utf-8")
        (mock_root / "Logs").mkdir(parents=True, exist_ok=True)
        (mock_root / "Logs" / "debug.log").write_text("logs", encoding="utf-8")
        (mock_root / "servis_deploy.log").write_text("log content", encoding="utf-8")
        (mock_root / "istorija_izrade.md").write_text("history", encoding="utf-8")
        (mock_root / "__chunk_data").write_text("chunks", encoding="utf-8")
        (mock_root / "support").mkdir(parents=True, exist_ok=True)
        (mock_root / "support" / "info.txt").write_text("support", encoding="utf-8")

        # 4.3 Pokretanje populate_staging za LEAN profil
        builder_lean = IsoBuilder(
            project_root=mock_root,
            output_path=mock_root / "test-lean.iso",
            profile="lean",
            no_prompt=True,
            dry_run=False
        )
        logs_lean = []
        stats_lean = builder_lean.populate_staging(staging_dir, log_cb=lambda m: logs_lean.append(m))

        # 4.4 Verifikacija OBAVEZNIH elemenata u Lean stagingu (VMD, boot.wim, drajveri)
        vmd_present = (staging_dir / "Za disk 10+ gen" / "iaStorVD.inf").exists()
        oem_drivers_present = (staging_dir / "sources" / "$OEM$" / "$1" / "setup" / "Drivers" / "net.inf").exists()
        boot_wim_present = (staging_dir / "sources" / "boot.wim").exists()
        autounattend_present = (staging_dir / "autounattend.xml").exists()
        accelerate_present = (staging_dir / "GWADT-AcceleratePE.cmd").exists()

        lean_essentials_ok = vmd_present and oem_drivers_present and boot_wim_present and autounattend_present and accelerate_present
        tracker.record(
            "Staging Filteri",
            "Uključivanje VMD drajvera i sistemskih fajlova u Lean profilu",
            lean_essentials_ok,
            f"VMD: {vmd_present}, NetDrivers: {oem_drivers_present}, boot.wim: {boot_wim_present}"
        )

        # 4.5 Verifikacija STRIKTNOG IZOSTAVLJANJA Programi.tar u Lean profilu
        root_tar_absent = not (staging_dir / "Programi.tar").exists()
        oem_tar_absent = not (staging_dir / "sources" / "$OEM$" / "$1" / "setup" / "Programi.tar").exists()
        tracker.record(
            "Staging Filteri",
            "Isključivanje Programi.tar u Lean profilu (ušteda 4.45 GB)",
            root_tar_absent and oem_tar_absent,
            f"Korenski Programi.tar odsutan: {root_tar_absent}, OEM Programi.tar odsutan: {oem_tar_absent}"
        )

        # 4.6 Verifikacija UKLJUČIVANJA Programi.tar u Full profilu
        with tempfile.TemporaryDirectory() as tmp_staging_full:
            staging_full = Path(tmp_staging_full)
            builder_full = IsoBuilder(
                project_root=mock_root,
                output_path=mock_root / "test-full.iso",
                profile="full",
                no_prompt=True,
                dry_run=False
            )
            builder_full.populate_staging(staging_full)
            root_tar_present_full = (staging_full / "Programi.tar").exists()
            vmd_present_full = (staging_full / "Za disk 10+ gen" / "iaStorVD.inf").exists()
            tracker.record(
                "Staging Filteri",
                "Uključivanje Programi.tar i VMD drajvera u Full profilu",
                root_tar_present_full and vmd_present_full,
                f"Programi.tar u Full stagingu: {root_tar_present_full}, VMD prisutan: {vmd_present_full}"
            )

        # 4.7 Verifikacija STRIKTNOG ISKLJUČIVANJA suvišnih elemenata
        forbidden_present = (
            (staging_dir / "Zakrpe").exists() or
            (staging_dir / "Programi").exists() or
            (staging_dir / ".git").exists() or
            (staging_dir / ".agents").exists() or
            (staging_dir / "second_brain").exists() or
            (staging_dir / "wiki").exists() or
            (staging_dir / "USB Tool").exists() or
            (staging_dir / "Logs").exists() or
            (staging_dir / "servis_deploy.log").exists() or
            (staging_dir / "istorija_izrade.md").exists() or
            (staging_dir / "__chunk_data").exists() or
            (staging_dir / "support").exists()
        )
        tracker.record(
            "Staging Filteri",
            "Striktno isključivanje suvišnih foldera/fajlova",
            not forbidden_present,
            "Zakrpe, Programi, .git, wiki, USB Tool, Logs, istorija_izrade uspešno eliminisani"
        )


def test_section_no_prompt_logic(tracker: TestResultTracker):
    """TEST 5: Verifikacija No-Prompt logike."""
    print("\n[TEST 5] Verifikacija No-Prompt logike (bootfix.bin & efisys_noprompt.bin)...")

    with tempfile.TemporaryDirectory() as tmp_mock_root:
        mock_root = Path(tmp_mock_root)

        # Priprema minimalnog stabla za test no_prompt
        (mock_root / "boot").mkdir(parents=True, exist_ok=True)
        (mock_root / "boot" / "etfsboot.com").write_text("etfs", encoding="utf-8")
        (mock_root / "boot" / "bootfix.bin").write_text("bootfix content", encoding="utf-8")
        (mock_root / "efi" / "microsoft" / "boot").mkdir(parents=True, exist_ok=True)
        (mock_root / "efi" / "microsoft" / "boot" / "efisys.bin").write_text("efisys", encoding="utf-8")
        (mock_root / "efi" / "microsoft" / "boot" / "efisys_noprompt.bin").write_text("efisys_np", encoding="utf-8")
        (mock_root / "sources" / "$OEM$").mkdir(parents=True, exist_ok=True)
        (mock_root / "sources" / "install.wim").write_text("wim", encoding="utf-8")
        (mock_root / "sources" / "boot.wim").write_text("bootwim", encoding="utf-8")
        (mock_root / "autounattend.xml").write_text("<xml/>", encoding="utf-8")
        (mock_root / "FirstLogon.cmd").write_text("fl", encoding="utf-8")
        (mock_root / "GWADT-AcceleratePE.cmd").write_text("accel", encoding="utf-8")
        (mock_root / "Wifi-Servis.ini").write_text("wifi", encoding="utf-8")
        (mock_root / "version.json").write_text('{"os_build_target": "10.0.26200.8037"}', encoding="utf-8")
        (mock_root / "Za disk 10+ gen").mkdir(parents=True, exist_ok=True)
        (mock_root / "Za disk 10+ gen" / "iaStorVD.inf").write_text("vmd inf", encoding="utf-8")
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Drivers").mkdir(parents=True, exist_ok=True)
        (mock_root / "sources" / "$OEM$" / "$1" / "setup" / "Drivers" / "net.inf").write_text("net inf", encoding="utf-8")

        # 5.1 Test sa no_prompt = True: bootfix.bin NE SME dospeti u staging
        with tempfile.TemporaryDirectory() as tmp_stage_np:
            builder_np = IsoBuilder(project_root=mock_root, no_prompt=True, dry_run=True)
            builder_np.populate_staging(Path(tmp_stage_np))
            has_bootfix_np = (Path(tmp_stage_np) / "boot" / "bootfix.bin").exists()
            tracker.record(
                "No-Prompt Logika",
                "Uklanjanje 'bootfix.bin' kada je no_prompt=True",
                not has_bootfix_np,
                f"bootfix.bin u stagingu: {has_bootfix_np} (očekivano: False)"
            )

        # 5.2 Test sa no_prompt = False: bootfix.bin MORA ostati u stagingu
        with tempfile.TemporaryDirectory() as tmp_stage_prompt:
            builder_p = IsoBuilder(project_root=mock_root, no_prompt=False, dry_run=True)
            builder_p.populate_staging(Path(tmp_stage_prompt))
            has_bootfix_p = (Path(tmp_stage_prompt) / "boot" / "bootfix.bin").exists()
            tracker.record(
                "No-Prompt Logika",
                "Očuvanje 'bootfix.bin' kada je no_prompt=False",
                has_bootfix_p,
                f"bootfix.bin u stagingu: {has_bootfix_p} (očekivano: True)"
            )

        # 5.3 Verifikacija odabira EFI slike u komandi
        res_np = builder_np.build_iso()
        cmd_np = res_np.get("command", "")
        tracker.record(
            "No-Prompt Logika",
            "Odabir 'efisys_noprompt.bin' za EFI butovanje",
            "efisys_noprompt.bin" in cmd_np,
            "efisys_noprompt.bin uspešno prosleđen u EFI parametre"
        )

        res_p = builder_p.build_iso()
        cmd_p = res_p.get("command", "")
        tracker.record(
            "No-Prompt Logika",
            "Odabir 'efisys.bin' kada je prompt eksplicitno uključen",
            "efisys.bin" in cmd_p and "efisys_noprompt.bin" not in cmd_p,
            "efisys.bin uspešno prosleđen u standardnom prompt modu"
        )


def test_section_source_validation_and_disk(tracker: TestResultTracker):
    """TEST 6: Validacija izvora i sistemskih resursa."""
    print("\n[TEST 6] Validacija izvornih fajlova, ISO motora i prostora na disku...")

    # 6.1 Validacija izvora na stvarnom projektu (mora vratiti 0 grešaka)
    real_builder = IsoBuilder(project_root=PROJECT_ROOT, dry_run=True)
    real_errors = real_builder.validate_sources()
    tracker.record(
        "Validacija Izvora",
        "Verifikacija svih obaveznih fajlova na trenutnom repozitorijumu",
        len(real_errors) == 0,
        f"Grešaka detektovano: {len(real_errors)}" if real_errors else "Sve komponente pronađene"
    )

    # 6.2 Validacija izvora na praznom folderu (mora prijaviti greške)
    with tempfile.TemporaryDirectory() as empty_dir:
        fake_builder = IsoBuilder(project_root=Path(empty_dir), dry_run=True)
        fake_errors = fake_builder.validate_sources()
        tracker.record(
            "Validacija Izvora",
            "Detekcija nedostajućih komponenti na nevalidnom izvoru",
            len(fake_errors) >= 4,
            f"Uspešno uhvaćeno {len(fake_errors)} grešaka strukture"
        )

    # 6.3 Provera detekcije ISO motora
    try:
        engine_type, engine_bin = detect_iso_engine("auto")
        tracker.record(
            "ISO Engine",
            f"Detekcija sistemskog ISO alata ({engine_type.upper()})",
            engine_type in ["host_genisoimage", "host_xorriso", "docker_xorriso", "xorriso"] and Path(engine_bin).exists(),
            f"Alat: {engine_type} na {engine_bin}"
        )
    except Exception as e:
        tracker.record("ISO Engine", "Detekcija sistemskog ISO alata", False, str(e))

    # 6.4 Validacija UDF motora i odbacivanje nepoznatog motora
    try:
        udf_type, udf_bin = detect_iso_engine("udf")
        tracker.record(
            "ISO Engine",
            "Detekcija i validacija UDF motora (genisoimage/udf)",
            udf_type == "host_genisoimage" and Path(udf_bin).exists(),
            f"UDF motor: {udf_type} ({udf_bin})"
        )
    except Exception as e:
        tracker.record("ISO Engine", "Detekcija i validacija UDF motora (genisoimage/udf)", False, str(e))

    try:
        detect_iso_engine("nepoznat_invalid_motor")
        tracker.record(
            "ISO Engine",
            "Odbacivanje nepoznatog motora (ValueError)",
            False,
            "Greška: nepoznat motor nije odbačen!"
        )
    except ValueError as ve:
        tracker.record(
            "ISO Engine",
            "Odbacivanje nepoznatog motora (ValueError)",
            True,
            f"Uspešno odbačen: {str(ve).splitlines()[0]}"
        )

    # 6.5 Revizija El Torito kataloga (audit_boot_catalog)
    # Testiramo na nepostojećem fajlu i na primeru
    audit_fake_ok, audit_fake_msg = audit_boot_catalog(Path("/tmp/nonexistent_test.iso"))
    tracker.record(
        "El Torito Audit",
        "Detekcija nepostojećeg ISO fajla u audit_boot_catalog",
        audit_fake_ok is False,
        f"Rezultat: {audit_fake_msg}"
    )

    # 6.6 Provera check_disk_space funkcije
    ok_small, free_gb, total_gb = check_disk_space(PROJECT_ROOT, min_gb=0.1)
    ok_huge, _, _ = check_disk_space(PROJECT_ROOT, min_gb=999999.0)
    tracker.record(
        "Provera Resursa",
        "Kalkulacija i prag slobodnog prostora na disku",
        ok_small is True and ok_huge is False,
        f"Slobodno: {free_gb:.1f} GB / Ukupno: {total_gb:.1f} GB"
    )


def test_section_iso_build_manager(tracker: TestResultTracker):
    """TEST 7: Testiranje asinhronog menadžera (USB Tool/core/iso_builder.py)."""
    print("\n[TEST 7] Testiranje asinhronog IsoBuildManager-a...")

    manager = IsoBuildManager(project_root=PROJECT_ROOT)

    # 7.1 Provera get_status() strukture
    status = manager.get_status()
    required_keys = [
        "active", "status", "progress", "step", "output_file",
        "iso_size_mb", "iso_size_gb", "sha256", "md5",
        "error", "start_time", "duration", "logs"
    ]
    all_keys_present = all(k in status for k in required_keys)
    tracker.record(
        "IsoBuildManager",
        "get_status() struktura rečnika i svi obavezni ključevi",
        all_keys_present and status["status"] == "idle" and status["active"] is False,
        f"Ključeva: {len(status)}/13, Inicijalni status: {status['status']}"
    )

    # 7.2 Test asinhronog pokretanja u dry-run modu
    with tempfile.TemporaryDirectory() as tmp_out:
        ok, msg = manager.start_build(output_dir=tmp_out, dry_run=True, no_prompt=True)
        tracker.record(
            "IsoBuildManager",
            "start_build(dry_run=True) inicijalizacija pozadinske niti",
            ok is True,
            f"Odgovor: {msg}"
        )

        # Čekamo da pozadinska nit završi (dry-run traje delić sekunde)
        deadline = time.time() + 5.0
        finished = False
        while time.time() < deadline:
            st = manager.get_status()
            if not st["active"] and st["status"] == "done":
                finished = True
                break
            time.sleep(0.05)

        final_st = manager.get_status()
        tracker.record(
            "IsoBuildManager",
            "Završetak asinhronog rada i status 'done' (100%)",
            finished and final_st["progress"] == 100 and final_st["error"] is None,
            f"Status: {final_st['status']}, Progres: {final_st['progress']}%, Trajanje: {final_st['duration']}s"
        )

    # 7.3 Test zaštite od konkurentnog pokretanja
    with manager.lock:
        manager.state["active"] = True
    try:
        ok_dup, msg_dup = manager.start_build(dry_run=True)
        tracker.record(
            "IsoBuildManager",
            "Zaštita od paralelnog/duplog pokretanja (Concurrency Lock)",
            ok_dup is False and "već u toku" in msg_dup,
            f"Odbijeno sa porukom: '{msg_dup}'"
        )
    finally:
        with manager.lock:
            manager.state["active"] = False


def test_section_rest_api(tracker: TestResultTracker):
    """TEST 8: Provera REST API ruta u USB Tool/app.py."""
    print("\n[TEST 8] Provera USB Tool REST API integracije (app.py)...")

    # Podižemo lokalni test server na dinamičkom slobodnom portu (port 0)
    server = ThreadingHTTPServer(("127.0.0.1", 0), UsbToolRequestHandler)
    assigned_port = server.server_address[1]
    base_url = f"http://127.0.0.1:{assigned_port}"

    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()
    time.sleep(0.1)

    try:
        # 8.1 Test GET /api/build-iso/status
        req_status = Request(f"{base_url}/api/build-iso/status", method="GET")
        with urlopen(req_status, timeout=5) as resp:
            status_code = resp.status
            content_type = resp.headers.get("Content-Type", "")
            body = json.loads(resp.read().decode("utf-8"))

        valid_response = (
            status_code == 200 and
            "application/json" in content_type and
            "status" in body and
            "progress" in body
        )
        tracker.record(
            "REST API",
            "GET /api/build-iso/status (HTTP 200 JSON)",
            valid_response,
            f"Status: {status_code}, Stanje: {body.get('status')}"
        )

        # 8.2 Test POST /api/build-iso sa dry_run=True
        with tempfile.TemporaryDirectory() as tmp_api_out:
            payload = json.dumps({
                "output_dir": tmp_api_out,
                "dry_run": True,
                "no_prompt": True,
                "force": True
            }).encode("utf-8")

            req_post = Request(
                f"{base_url}/api/build-iso",
                data=payload,
                headers={"Content-Type": "application/json"},
                method="POST"
            )
            with urlopen(req_post, timeout=5) as resp_post:
                post_code = resp_post.status
                post_data = json.loads(resp_post.read().decode("utf-8"))

            tracker.record(
                "REST API",
                "POST /api/build-iso sa parametrima (HTTP 200 JSON)",
                post_code == 200 and post_data.get("success") is True,
                f"Poruka: '{post_data.get('message')}'"
            )

            # Provera da se status ažurira u 'done'
            time.sleep(0.3)
            with urlopen(Request(f"{base_url}/api/build-iso/status"), timeout=5) as r2:
                status_after = json.loads(r2.read().decode("utf-8"))

            tracker.record(
                "REST API",
                "Ažuriranje statusa nakon POST zahteva",
                status_after.get("status") in ["done", "staging", "building"],
                f"Tekući status: {status_after.get('status')}, Progres: {status_after.get('progress')}%"
            )

        # 8.3 Test nepostojeće rute (mora vratiti 404)
        try:
            with urlopen(Request(f"{base_url}/api/build-iso/nepostojeca-ruta"), timeout=5) as r_err:
                tracker.record("REST API", "Rukovanje nepoznatom rutom (404 Not Found)", False, "Trebalo je vratiti 404")
        except HTTPError as he:
            tracker.record("REST API", "Rukovanje nepoznatom rutom (404 Not Found)", he.code == 404, f"Vraćen HTTP {he.code}")

    finally:
        server.shutdown()
        server.server_close()


def test_section_web_gui(tracker: TestResultTracker):
    """TEST 9: Provera Web GUI datoteka (index.html, app.js, style.css)."""
    print("\n[TEST 9] Verifikacija Web GUI datoteka (HTML, JS, CSS)...")

    html_file = USB_TOOL_DIR / "gui" / "index.html"
    js_file = USB_TOOL_DIR / "gui" / "app.js"
    css_file = USB_TOOL_DIR / "gui" / "style.css"

    # 9.1 HTML elementi
    html_content = html_file.read_text(encoding="utf-8") if html_file.exists() else ""
    html_ok = (
        'data-tab="tabVentoyIso"' in html_content and
        'id="tabVentoyIso"' in html_content and
        'id="inputIsoOutputDir"' in html_content and
        'id="btnStartBuildIso"' in html_content and
        'id="isoBuildProgSection"' in html_content and
        'id="isoProgressBar"' in html_content and
        'id="isoResultCard"' in html_content and
        'id="isoResultSha256"' in html_content and
        'id="isoResultMd5"' in html_content
    )
    tracker.record(
        "Web GUI Integritet",
        "index.html sadrži Ventoy ISO tab i sve elemente",
        html_ok,
        "Tab, inputi, progress bar, logs i result card prisutni"
    )

    # 9.2 JS logika
    js_content = js_file.read_text(encoding="utf-8") if js_file.exists() else ""
    js_ok = (
        '/api/build-iso' in js_content and
        '/api/build-iso/status' in js_content and
        'btnStartBuildIso' in js_content and
        'startBuildIso' in js_content and
        'startIsoBuildPolling' in js_content
    )
    tracker.record(
        "Web GUI Integritet",
        "app.js implementira API pozive i dinamički polling",
        js_ok,
        "Event listeneri, fetch pozivi i tajmer za progres verifikovani"
    )

    # 9.3 CSS stilovi
    css_content = css_file.read_text(encoding="utf-8") if css_file.exists() else ""
    css_ok = (
        '.iso-result-details' in css_content and
        '.iso-result-row' in css_content and
        '.hash-code' in css_content
    )
    tracker.record(
        "Web GUI Integritet",
        "style.css sadrži posvećene stilove za ISO generator",
        css_ok,
        "Klase za karticu rezultata, heš oznake i interaktivne redove definisane"
    )


def main():
    print("=" * 80)
    print("      GWADT QA SUITE - Sveobuhvatna Verifikacija Ventoy ISO Generatora")
    print("=" * 80)
    print(f"📍 Koren repozitorijuma: {PROJECT_ROOT}")
    print(f"⚙️  Python interpreter:   {sys.executable}")
    print(f"⏱️  Datum i vreme:        {time.strftime('%Y-%m-%d %H:%M:%S')}")

    tracker = TestResultTracker()

    test_section_version_detection(tracker)
    test_section_cli_and_dry_run(tracker)
    test_section_bash_wrapper(tracker)
    test_section_content_filters(tracker)
    test_section_no_prompt_logic(tracker)
    test_section_source_validation_and_disk(tracker)
    test_section_iso_build_manager(tracker)
    test_section_rest_api(tracker)
    test_section_web_gui(tracker)

    tracker.print_summary_table()

    return 0 if tracker.all_passed() else 1


if __name__ == "__main__":
    sys.exit(main())
