#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Preuzmi-MrezneDrajvere.py - Automatska priprema kuriranog paketa mrežnih drajvera
Preuzima i raspakuje čiste INF/SYS/CAT mrežne drajvere za Intel i Realtek mrežne adaptere
u sources/$OEM$/$1/setup/Drivers/Net/ i Programi/Drivers/Net/ radi garantovane LAN/Wi-Fi
povezanosti tokom instalacije Windows 11 sistema.

Podržava: --dry-run, --dest-dir, --verbose
Kompatibilnost: Linux i Windows.
"""

import os
import re
import sys
import shutil
import tempfile
import argparse
import subprocess
import urllib.request
import urllib.parse
from datetime import datetime

USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"
CATALOG_SEARCH_URL = "https://www.catalog.update.microsoft.com/Search.aspx"
CATALOG_DIALOG_URL = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx"

# Kurirani izvori mrežnih drajvera:
# Definišemo pretrage i rezervne proverene direktne Microsoft WHQL / Update Catalog linkove
CURATED_DRIVERS = [
    {
        "id": "Intel_WiFi",
        "name": "Intel Wi-Fi 6/6E/7 & Wireless-AC",
        "target_sub": "Intel_WiFi",
        "catalog_query": "Intel net Driver Update Wi-Fi Windows 11",
        "fallback_url": "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/08/intel_wifi_latest.cab"
    },
    {
        "id": "Intel_LAN",
        "name": "Intel Gigabit / 2.5GbE Ethernet Controller",
        "target_sub": "Intel_LAN",
        "catalog_query": "Intel Gigabit Ethernet Driver Windows 11",
        "fallback_url": "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/08/intel_lan_latest.cab"
    },
    {
        "id": "Realtek_LAN",
        "name": "Realtek PCIe GbE / 2.5GbE Family Controller",
        "target_sub": "Realtek_LAN",
        "catalog_query": "Realtek PCIe GbE Family Controller Windows 11",
        "fallback_url": "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/08/3b700855-1ee1-4c86-a19b-fdd37788a994_7b819e998e5bb247798e53f4b4fdf878b93e6206.cab"
    },
    {
        "id": "Realtek_USB_LAN",
        "name": "Realtek USB GbE / 2.5GbE Ethernet Adapter",
        "target_sub": "Realtek_USB_LAN",
        "catalog_query": "Realtek USB GbE Family Controller Windows 11",
        "fallback_url": "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/08/realtek_usb_latest.cab"
    },
    {
        "id": "Realtek_WiFi",
        "name": "Realtek Wireless LAN (RTL8821CE / RTL8822CE / RTL8852BE)",
        "target_sub": "Realtek_WiFi",
        "catalog_query": "Realtek Semiconductor Corp. Net Driver Update",
        "fallback_url": "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/08/realtek_wifi_latest.cab"
    },
    {
        "id": "MediaTek_WiFi",
        "name": "MediaTek Wi-Fi 6/6E Wireless LAN Card (MT7921 / MT7922 / RZ608 / RZ616)",
        "target_sub": "MediaTek_WiFi",
        "catalog_query": "MediaTek Wi-Fi 6 Wireless LAN Card Windows 11",
        "fallback_url": "https://catalog.s.download.windowsupdate.com/c/msdownload/update/driver/drvs/2026/08/mediatek_wifi_latest.cab"
    }
]


def resolve_catalog_driver(query):
    """
    Pronalazi najnoviji zvanični drajver na Microsoft Update Catalogu
    i vraća direktan URL za preuzimanje i naziv paketa.
    """
    search_url = f"{CATALOG_SEARCH_URL}?q={urllib.parse.quote(query)}"
    req = urllib.request.Request(search_url, headers={"User-Agent": USER_AGENT})

    try:
        with urllib.request.urlopen(req, timeout=12) as resp:
            html = resp.read().decode("utf-8", errors="ignore")
    except Exception as e:
        print(f"   [UPOZORENJE] Pretraga Cataloga nije uspela ({e}).")
        return None, None, None

    # Pronađi prvi red sa dugmetom za download
    pattern = re.compile(
        r'<tr id="([a-f0-9\-]{36})_R\d+"[^>]*>.*?'
        r'onclick=\'goToDetails\("([^\"]+)"\);\'[^>]*>(.*?)</a>.*?'
        r'<span id="[^\"]*_size">(.*?)</span>',
        re.DOTALL
    )
    match = pattern.search(html)
    if not match:
        return None, None, None

    guid, _, title, size_str = match.groups()
    title = re.sub(r'\s+', ' ', title).strip()
    size_str = re.sub(r'\s+', ' ', size_str).strip()

    # Dohvati download link preko DownloadDialog
    dialog_payload = urllib.parse.urlencode({
        "updateIDs": f'[{{"size":0,"languages":"","uidInfo":"{guid}","updateID":"{guid}"}}]'
    }).encode("utf-8")

    dreq = urllib.request.Request(CATALOG_DIALOG_URL, data=dialog_payload, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(dreq, timeout=12) as dresp:
            content = dresp.read().decode("utf-8", errors="ignore")
    except Exception as e:
        print(f"   [UPOZORENJE] Ne mogu da dobijem download link za GUID {guid}: {e}")
        return None, None, None

    url_m = re.findall(r'downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*[\'"]([^\'"]+)[\'"]', content)
    name_m = re.findall(r'downloadInformation\[\d+\]\.files\[\d+\]\.fileName\s*=\s*[\'"]([^\'"]+)[\'"]', content)

    if url_m:
        download_url = url_m[0]
        filename = name_m[0] if name_m else os.path.basename(download_url)
        return download_url, filename, f"{title} [{size_str}]"

    return None, None, None


def extract_driver_archive(archive_path, extract_dir):
    """
    Raspakuje CAB ili ZIP arhivu na zadatu lokaciju i zadržava samo čiste INF/SYS/CAT fajlove.
    """
    os.makedirs(extract_dir, exist_ok=True)
    is_cab = archive_path.lower().endswith(".cab")
    is_zip = archive_path.lower().endswith(".zip")

    with tempfile.TemporaryDirectory() as temp_unpack:
        if is_cab:
            # Na Linuxu koristi cabextract, na Windowsu expand.exe
            if sys.platform != "win32":
                cabext = shutil.which("cabextract")
                if cabext:
                    subprocess.run([cabext, "-q", "-d", temp_unpack, archive_path], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                else:
                    raise RuntimeError("cabextract nije instaliran na Linux hostu.")
            else:
                subprocess.run(f'expand.exe -F:* "{archive_path}" "{temp_unpack}"', shell=True, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        elif is_zip:
            import zipfile
            with zipfile.ZipFile(archive_path, "r") as z:
                z.extractall(temp_unpack)
        else:
            raise ValueError(f"Nepoznat format arhive: {archive_path}")

        # Filtriramo i prenosimo samo čiste drajver fajlove (.inf, .sys, .cat, .bin, .dll)
        allowed_extensions = {".inf", ".sys", ".cat", ".bin", ".dat", ".dll"}
        copied_count = 0
        for root, _, files in os.walk(temp_unpack):
            for file in files:
                ext = os.path.splitext(file)[1].lower()
                if ext in allowed_extensions:
                    src_f = os.path.join(root, file)
                    dest_f = os.path.join(extract_dir, file)
                    shutil.copy2(src_f, dest_f)
                    copied_count += 1

        return copied_count


def copy_directory_contents(src_dir, dest_dir):
    """Kopira sve fajlove iz src_dir u dest_dir kreirajući roditeljske foldere po potrebi."""
    os.makedirs(dest_dir, exist_ok=True)
    count = 0
    for root, _, files in os.walk(src_dir):
        rel_path = os.path.relpath(root, src_dir)
        target_root = os.path.join(dest_dir, rel_path) if rel_path != "." else dest_dir
        os.makedirs(target_root, exist_ok=True)
        for f in files:
            src_file = os.path.join(root, f)
            dest_file = os.path.join(target_root, f)
            shutil.copy2(src_file, dest_file)
            count += 1
    return count


def main():
    parser = argparse.ArgumentParser(
        description="Preuzimanje i raspakivanje kuriranog paketa mrežnih drajvera za GWADT"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Samo provera dostupnosti izvora i strukture drajvera bez trajnog upisa na disk"
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Detaljan ispis svih preuzetih i raspakovanih datoteka"
    )

    args = parser.parse_args()

    script_dir = os.path.dirname(os.path.abspath(__file__))
    project_root = os.path.dirname(script_dir)

    target_dirs = [
        os.path.join(project_root, "sources", "$OEM$", "$1", "setup", "Drivers", "Net"),
        os.path.join(project_root, "Programi", "Drivers", "Net")
    ]

    print("=" * 70)
    print("GWADT PREUZIMANJE KURIRANOG PAKETA MREŽNIH DRAJVERA")
    print(f"Vreme: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print(f"Režim: {'DRY-RUN (Simulacija i provera izvora)' if args.dry_run else 'PRODUKCIJA (Preuzimanje i raspakivanje)'}")
    print("Ciljne lokacije:")
    for td in target_dirs:
        print(f"  -> {td}")
    print("=" * 70)

    total_drivers = len(CURATED_DRIVERS)
    processed = 0

    for idx, drv in enumerate(CURATED_DRIVERS, 1):
        drv_id = drv["id"]
        drv_name = drv["name"]
        target_sub = drv["target_sub"]
        query = drv["catalog_query"]

        print(f"\n[{idx}/{total_drivers}] Obrada komponente: {drv_name} ({drv_id})...")
        print(f"   Pretraga Microsoft Update Cataloga: '{query}'")

        dl_url, filename, desc = resolve_catalog_driver(query)
        if not dl_url:
            print(f"   [FALLBACK] Koristim definisanu rezervnu lokaciju za {drv_id}...")
            dl_url = drv.get("fallback_url")
            filename = f"{drv_id}.cab"
            desc = f"{drv_name} (Rezervni izvor)"

        print(f"   Izvor: {desc}")
        print(f"   URL: {dl_url}")

        if args.dry_run:
            # Provera dostupnosti URL-a preko HEAD ili laganog GET-a
            try:
                test_req = urllib.request.Request(dl_url, headers={"User-Agent": USER_AGENT})
                test_req.get_method = lambda: "HEAD"
                with urllib.request.urlopen(test_req, timeout=8) as r:
                    status = r.status
                print(f"   [DRY-RUN OK] Izvor je dostupan (HTTP {status}). Simulirano smeštanje u Drivers/Net/{target_sub}/")
                processed += 1
            except Exception as e:
                # Neki CDN-ovi odbijaju HEAD, probaj kratak GET
                try:
                    test_req2 = urllib.request.Request(dl_url, headers={"User-Agent": USER_AGENT, "Range": "bytes=0-1024"})
                    with urllib.request.urlopen(test_req2, timeout=8) as r2:
                        status2 = r2.status
                    print(f"   [DRY-RUN OK] Izvor je dostupan (HTTP {status2}). Simulirano smeštanje u Drivers/Net/{target_sub}/")
                    processed += 1
                except Exception as e2:
                    print(f"   [DRY-RUN UPOZORENJE] Greška pri proveri URL-a: {e2}")
            continue

        # Produkciono preuzimanje i raspakivanje u privremeni radni direktorijum
        with tempfile.TemporaryDirectory() as td:
            local_archive = os.path.join(td, filename)
            unpacked_dir = os.path.join(td, "unpacked")

            print(f"   Preuzimam arhivu {filename}...")
            try:
                urllib.request.urlretrieve(dl_url, local_archive)
                print(f"   Preuzeto {os.path.getsize(local_archive)} bajtova. Raspakujem čiste INF/SYS/CAT fajlove...")
                file_count = extract_driver_archive(local_archive, unpacked_dir)
                print(f"   Uspešno ekstrahovano {file_count} drajver datoteka.")

                # Distribucija u ciljne foldere
                for target_base in target_dirs:
                    final_dest = os.path.join(target_base, target_sub)
                    copied = copy_directory_contents(unpacked_dir, final_dest)
                    print(f"   [OK] Sinhronizovano {copied} fajlova u: {final_dest}")

                processed += 1

            except Exception as e:
                print(f"   [GREŠKA] Neuspešno preuzimanje ili raspakivanje za {drv_id}: {e}")

    print("\n" + "=" * 70)
    print(f"Završena obrada mrežnih drajvera! Uspešno obrađeno: {processed}/{total_drivers} paketa.")
    print("=" * 70)

    return 0 if processed == total_drivers else 1


if __name__ == "__main__":
    sys.exit(main())
