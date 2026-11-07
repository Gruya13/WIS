# -*- coding: utf-8 -*-
"""
iso_finder.py - Automatski pronalazač Windows 11 ISO/ESD slika i Microsoft izvora
Skenira lokalne diskove računara i nudi zvanične Microsoft kanale za preuzimanje.
"""

import os
import sys
import json
import urllib.parse
from pathlib import Path
from datetime import datetime

def scan_local_iso_images():
    """
    Automatski pretražuje lokalne diskove radne stanice (Downloads, Storage, Home)
    za postojeće Windows 11 ISO i ESD instalacione fajlove.
    """
    search_dirs = [
        Path.home() / "Downloads",
        Path("/home/gru/Storage"),
        Path("/home/gru/Storage/gwadt_refresh_work"),
        Path.home(),
        Path("/tmp")
    ]
    # Ako radi unutar Docker kontejnera sa mapiranim host folderima
    for env_k in ["GWADT_HOST_DOWNLOADS", "GWADT_HOST_STORAGE"]:
        env_val = os.environ.get(env_k)
        if env_val:
            search_dirs.insert(0, Path(env_val))

    found = []
    seen_paths = set()

    for d in search_dirs:
        try:
            if not d.exists() or not os.access(str(d), os.R_OK):
                continue

            for ext in ["*.iso", "*.esd"]:
                for p in d.glob(ext):
                    if p.is_file() and str(p) not in seen_paths:
                        name_lower = p.name.lower()
                        # Filtriramo samo one koji liče na Windows ili instalaciju
                        if any(k in name_lower for k in ["win", "windows", "install", "11", "24h2", "23h2", "22h2", "iso"]):
                            try:
                                stat = p.stat()
                                size_gb = round(stat.st_size / (1024 ** 3), 2)
                                if size_gb >= 2.0: # Pravi Windows 11 ISO je obično > 3.5 GB
                                    mtime = datetime.fromtimestamp(stat.st_mtime).strftime("%Y-%m-%d %H:%M")
                                    found.append({
                                        "name": p.name,
                                        "path": str(p.resolve()),
                                        "size_gb": size_gb,
                                        "size_bytes": stat.st_size,
                                        "modified": mtime,
                                        "type": "ISO Slika" if p.suffix.lower() == ".iso" else "ESD Paket"
                                    })
                                    seen_paths.add(str(p))
                            except Exception:
                                continue
        except Exception:
            continue

    # Sortiramo po datumu, najnoviji na vrhu
    found.sort(key=lambda x: x["size_bytes"], reverse=True)
    return found

def get_microsoft_official_sources():
    """
    Vraća zvanične i proverene Microsoft kanale i preporučene URL-ove
    za preuzimanje najnovijeg Windows 11 izdanja.
    """
    return {
        "official_download_portal": "https://www.microsoft.com/software-download/windows11",
        "uup_dump_portal": "https://uupdump.net/known.php?q=Windows+11",
        "ms_catalog_portal": "https://www.catalog.update.microsoft.com/Search.aspx?q=Windows+11+Cumulative+Update",
        "recommended_edition": "Windows 11 Pro (x64)",
        "instructions": [
            "1. Otvorite zvanični Microsoft portal (Software Download).",
            "2. Izaberite 'Windows 11 (multi-edition ISO)' i kliknite 'Download'.",
            "3. Kopirajte generisani direktan link i unesite ga u polje ispod ili sačuvajte ISO u ~/Downloads.",
            "4. Sistem će automatski prepoznati novi fajl i sabiti ga u Pre-Stage keš!"
        ]
    }
