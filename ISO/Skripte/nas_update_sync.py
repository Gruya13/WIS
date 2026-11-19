#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
nas_update_sync.py - Centralizovani NAS Update Sinhronizator za GWADT
Autonomno pretražuje, preuzima, verifikuje i rotira najnovije kumulativne
bezbednosne zakrpe (LCU) za Windows 11 x64 na lokalnom NAS skladištu.

Kompatibilnost: Linux (CentOS, Debian, Ubuntu, Synology/TrueNAS) i Windows.
Podržava: --nas-dir, --dry-run, --cron, --keep, --version
"""

import os
import re
import sys
import time
import base64
import hashlib
import logging
import argparse
import urllib.request
import urllib.parse
from datetime import datetime

DEFAULT_KEEP = 2
CATALOG_SEARCH_URL = "https://www.catalog.update.microsoft.com/Search.aspx"
CATALOG_DIALOG_URL = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx"
USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"

# Konfiguracija logera
logger = logging.getLogger("nas_update_sync")


def setup_logging(log_file=None, cron_mode=False):
    """Postavlja format i nivoe logovanja u zavisnosti od režima rada."""
    logger.setLevel(logging.INFO)
    logger.handlers.clear()

    formatter = logging.Formatter(
        "[%(asctime)s] [%(levelname)s] %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S"
    )

    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setFormatter(formatter)
    logger.addHandler(console_handler)

    if log_file:
        try:
            os.makedirs(os.path.dirname(os.path.abspath(log_file)), exist_ok=True)
            file_handler = logging.FileHandler(log_file, encoding="utf-8")
            file_handler.setFormatter(formatter)
            logger.addHandler(file_handler)
        except Exception as e:
            logger.warning(f"Ne mogu da otvorim log fajl {log_file}: {e}")


def load_nas_dir_from_ini(repo_root=None):
    """
    Pokušava da očita putanju za NAS skladište iz Wifi-Servis.ini sekcije [ServisNas].
    Ako nađe NasHost i ShareName, formira odgovarajuću putanju.
    """
    candidates = []
    if repo_root:
        candidates.append(os.path.join(repo_root, "Wifi-Servis.ini"))
        candidates.append(os.path.join(repo_root, "Skripte", "Wifi-Servis.ini"))
    candidates.extend([
        "Wifi-Servis.ini",
        os.path.join("Skripte", "Wifi-Servis.ini"),
        "/mnt/nas/gwadt-cache/Wifi-Servis.ini",
        "C:\\setup\\Wifi-Servis.ini"
    ])

    for c in candidates:
        if os.path.isfile(c):
            try:
                with open(c, "r", encoding="utf-8", errors="ignore") as f:
                    content = f.read()
                m = re.search(r'(?mi)^\s*\[ServisNas\]([\s\S]*?)(?:^\s*\[|\Z)', content)
                if m:
                    sec = m.group(1)
                    sub = ""
                    share = ""
                    m_sub = re.search(r'(?mi)^[ \t]*UpdatesSubfolder[ \t]*=[ \t]*([^\r\n#;]+)', sec)
                    if m_sub:
                        sub = m_sub.group(1).strip()
                    m_share = re.search(r'(?mi)^[ \t]*ShareName[ \t]*=[ \t]*([^\r\n#;]+)', sec)
                    if m_share:
                        share = m_share.group(1).strip()

                    # Na Linuxu najčešće montirano pod /mnt/nas/<share>/<subfolder>
                    if sys.platform != "win32":
                        test_mounts = [
                            f"/mnt/nas/{share}/{sub}",
                            f"/mnt/nas/gwadt-cache/{sub}",
                            f"/var/nas/{share}/{sub}",
                            f"./{sub}"
                        ]
                        for tm in test_mounts:
                            tm_clean = os.path.normpath(tm.replace("\\", "/"))
                            if os.path.isdir(tm_clean):
                                return tm_clean
                    else:
                        m_host = re.search(r'(?mi)^[ \t]*NasHost[ \t]*=[ \t]*([^\r\n#;]+)', sec)
                        host = m_host.group(1).strip() if m_host else ""
                        if host and share:
                            unc = f"\\\\{host}\\{share}\\{sub}"
                            return unc
            except Exception:
                pass
    return None


def fetch_catalog_search(query):
    """
    Izvršava pretragu Microsoft Update Catalog-a i vraća listu pronađenih stavki.
    """
    url = f"{CATALOG_SEARCH_URL}?q={urllib.parse.quote(query)}"
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            html = resp.read().decode("utf-8", errors="ignore")
    except Exception as e:
        logger.error(f"Greška pri pretrazi Microsoft Update Catalog-a za '{query}': {e}")
        return []

    # Parsiranje redova tabele
    # <tr id="<guid>_R<idx>" ...
    row_pattern = re.compile(
        r'<tr id="([a-f0-9\-]{36})_R\d+"[^>]*>.*?'
        r'onclick=\'goToDetails\("([^\"]+)"\);\'[^>]*>(.*?)</a>.*?'
        r'<td[^>]*>(.*?)</td>\s*'  # Products
        r'<td[^>]*>(.*?)</td>\s*'  # Classification
        r'<td[^>]*>(.*?)</td>\s*'  # Last Updated
        r'<td[^>]*>(.*?)</td>\s*'  # Version
        r'<td[^>]*>.*?<span id="[^\"]*_size">(.*?)</span>.*?'  # Size
        r'<span class="noDisplay" id="[^\"]*_originalSize">(\d+)</span>',  # Size in bytes
        re.DOTALL
    )

    items = []
    for m in row_pattern.finditer(html):
        guid, _, title, prod, classif, date_str, ver_str, size_str, size_bytes = m.groups()
        title_clean = re.sub(r'\s+', ' ', title).strip()
        prod_clean = re.sub(r'\s+', ' ', prod).strip()
        classif_clean = re.sub(r'\s+', ' ', classif).strip()
        date_clean = re.sub(r'\s+', ' ', date_str).strip()
        size_clean = re.sub(r'\s+', ' ', size_str).strip()

        # Izvuci KB broj iz naslova
        kb_match = re.search(r'\((KB\d+)\)', title_clean, re.I)
        kb_id = kb_match.group(1).upper() if kb_match else "KBUNKNOWN"

        items.append({
            "guid": guid,
            "title": title_clean,
            "kb": kb_id,
            "product": prod_clean,
            "classification": classif_clean,
            "date": date_clean,
            "size_str": size_clean,
            "size_bytes": int(size_bytes)
        })

    return items


def fetch_download_info(guid, target_kb=None):
    """
    Šalje POST zahtev na DownloadDialog.aspx za zadati GUID ažuriranja
    i vraća direktan URL ka .msu fajlu, tačan naziv fajla i SHA256 heš (hex).
    Ako postoji više fajlova (npr. delta/baseline prethodnici), prioritet ima
    fajl koji u imenu sadrži target_kb.
    """
    post_payload = urllib.parse.urlencode({
        "updateIDs": f'[{{"size":0,"languages":"","uidInfo":"{guid}","updateID":"{guid}"}}]'
    }).encode("utf-8")

    req = urllib.request.Request(
        CATALOG_DIALOG_URL,
        data=post_payload,
        headers={"User-Agent": USER_AGENT}
    )

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            content = resp.read().decode("utf-8", errors="ignore")
    except Exception as e:
        logger.error(f"Greška pri dohvatanju download informacija za GUID {guid}: {e}")
        return None

    # Ekstrakcija linkova i heševa iz JavaScript niza downloadInformation
    url_matches = re.findall(r'downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*[\'"]([^\'"]+)[\'"]', content)
    file_matches = re.findall(r'downloadInformation\[\d+\]\.files\[\d+\]\.fileName\s*=\s*[\'"]([^\'"]+)[\'"]', content)
    sha_matches = re.findall(r'downloadInformation\[\d+\]\.files\[\d+\]\.sha256\s*=\s*[\'"]([^\'"]+)[\'"]', content)

    if not url_matches:
        logger.warning(f"DownloadDialog nije vratio linkove za GUID {guid}.")
        return None

    # Preferiramo .msu fajl koji odgovara zadatom target_kb, ili prvi .msu fajl
    selected_idx = 0
    found_target = False
    if target_kb:
        clean_kb = target_kb.lower()
        for idx, u in enumerate(url_matches):
            fname = file_matches[idx] if idx < len(file_matches) else u
            if clean_kb in fname.lower() or clean_kb in u.lower():
                selected_idx = idx
                found_target = True
                break

    if not found_target:
        for idx, u in enumerate(url_matches):
            if u.lower().endswith(".msu"):
                selected_idx = idx
                break

    download_url = url_matches[selected_idx]
    file_name = file_matches[selected_idx] if selected_idx < len(file_matches) else os.path.basename(download_url)
    sha256_b64 = sha_matches[selected_idx] if selected_idx < len(sha_matches) else ""

    # Konverzija Base64 SHA256 u hex format za lakše poređenje
    sha256_hex = ""
    if sha256_b64:
        try:
            sha256_hex = base64.b64decode(sha256_b64).hex().lower()
        except Exception:
            pass

    return {
        "url": download_url,
        "filename": file_name,
        "sha256": sha256_hex
    }


def find_latest_lcu_updates(windows_versions=("25H2", "24H2"), include_dotnet=True):
    """
    Pronalazi najnoviju stabilnu kumulativnu nadogradnju (LCU) i .NET 3.5/4.8.1
    kumulativni paket za zadate verzije Windows 11 x64.
    Isključuje Preview, Dynamic i ARM64 verzije i deduplikuje po imenu fajla.
    """
    results = []
    seen_filenames = set()

    for ver in windows_versions:
        query = f"Cumulative Update for Windows 11 Version {ver} for x64-based Systems"
        logger.info(f"Pretražujem Microsoft Update Catalog za Windows 11 {ver} x64...")
        items = fetch_catalog_search(query)

        # Filtriranje samo stabilnih LCU ažuriranja
        valid_lcu = []
        for it in items:
            title = it["title"]
            # Mora sadržati Cumulative Update i verziju, a ne sme biti Preview, Safe OS ili Dynamic
            if "Cumulative Update" in title and ver.lower() in title.lower():
                if not any(skip in title.lower() for skip in ["preview", "safe os", "setup dynamic", "arm64", "dynamic", ".net framework"]):
                    valid_lcu.append(it)

        if not valid_lcu:
            # Fallback: ako nema striktnog podudaranja verzije u naslovu
            for it in items:
                title = it["title"]
                if "Cumulative Update" in title and not any(skip in title.lower() for skip in ["preview", "safe os", "setup dynamic", "arm64", ".net framework"]):
                    valid_lcu.append(it)
                    break

        if valid_lcu:
            top_item = valid_lcu[0]
            logger.info(f"Pronađena LCU zakrpa za {ver}: {top_item['title']} [{top_item['size_str']}] (Objavljeno: {top_item['date']})")

            # Dohvati download parametre
            dl_info = fetch_download_info(top_item["guid"], target_kb=top_item.get("kb"))
            if dl_info:
                top_item.update(dl_info)
                top_item["win_version"] = ver
                fname_key = top_item["filename"].lower()
                if fname_key not in seen_filenames:
                    seen_filenames.add(fname_key)
                    results.append(top_item)
                else:
                    logger.info(f"Paket {top_item['filename']} je već u listi za preuzimanje (deljeni LCU binarni fajl).")
            else:
                logger.warning(f"Ne mogu da preuzmem download link za {top_item['title']}.")
        else:
            logger.warning(f"Nisu pronađene stabilne LCU zakrpe za Windows 11 {ver}.")

        # Pretraga za .NET Framework 3.5 and 4.8.1 Cumulative Update za istu verziju
        if include_dotnet:
            ndp_query = f"Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version {ver} for x64"
            logger.info(f"Pretražujem Microsoft Update Catalog za .NET 3.5/4.8.1 ({ver} x64)...")
            ndp_items = fetch_catalog_search(ndp_query)
            valid_ndp = []
            for it in ndp_items:
                title = it["title"]
                if ".NET Framework" in title and ver.lower() in title.lower():
                    if not any(skip in title.lower() for skip in ["preview", "arm64"]):
                        valid_ndp.append(it)

            if valid_ndp:
                top_ndp = valid_ndp[0]
                logger.info(f"Pronađena .NET zakrpa za {ver}: {top_ndp['title']} [{top_ndp['size_str']}] (Objavljeno: {top_ndp['date']})")
                dl_ndp = fetch_download_info(top_ndp["guid"], target_kb=top_ndp.get("kb"))
                if dl_ndp:
                    top_ndp.update(dl_ndp)
                    top_ndp["win_version"] = ver
                    fname_key = top_ndp["filename"].lower()
                    if fname_key not in seen_filenames:
                        seen_filenames.add(fname_key)
                        results.append(top_ndp)

    return results


def verify_file_sha256(filepath, expected_sha256_hex):
    """Računa SHA256 heš lokalnog fajla i upoređuje sa očekivanim."""
    if not os.path.isfile(filepath):
        return False
    if not expected_sha256_hex:
        return True  # Ako nema očekivanog heša, preskačemo striktnu verifikaciju

    sha = hashlib.sha256()
    try:
        with open(filepath, "rb") as f:
            while chunk := f.read(1048576):  # 1MB chunks
                sha.update(chunk)
        calc_hex = sha.hexdigest().lower()
        return calc_hex == expected_sha256_hex.lower()
    except Exception as e:
        logger.error(f"Greška pri verifikaciji SHA256 za {filepath}: {e}")
        return False


def download_file_stream(url, destination_path, expected_size=0, expected_sha256="", cron_mode=False, threads=12):
    """
    Preuzima fajl u višenitnom HTTP Range režimu (ili stream fallback-u) sa
    dinamičkim progress barom i atomičnim preimenovanjem.
    """
    import concurrent.futures
    import threading

    dest_dir = os.path.dirname(os.path.abspath(destination_path))
    os.makedirs(dest_dir, exist_ok=True)

    temp_path = destination_path + ".part"
    filename = os.path.basename(destination_path)

    # Ako konačni fajl već postoji i ispravan je, preskačemo skidanje
    if os.path.isfile(destination_path):
        if expected_sha256:
            logger.info(f"Verifikujem postojeći fajl: {filename}...")
            if verify_file_sha256(destination_path, expected_sha256):
                logger.info(f"[OK] Fajl {filename} već postoji na NAS-u i SHA256 heš je validan. Preskačem preuzimanje.")
                return True
            else:
                logger.warning(f"Postojeći fajl {filename} ima neispravan heš. Preuzimam ponovo...")
        elif expected_size > 0 and os.path.getsize(destination_path) == expected_size:
            logger.info(f"[OK] Fajl {filename} već postoji sa tačnom veličinom ({expected_size} bajtova). Preskačem.")
            return True

    logger.info(f"Započinjem preuzimanje: {filename}...")

    # Provera Content-Length i Accept-Ranges za višenitno preuzimanje
    total_size = 0
    accept_ranges = False
    try:
        head_req = urllib.request.Request(url, method="HEAD", headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(head_req, timeout=15) as h_resp:
            total_size = int(h_resp.headers.get("Content-Length", 0))
            accept_ranges = "bytes" in h_resp.headers.get("Accept-Ranges", "").lower()
    except Exception:
        pass

    if accept_ranges and total_size > 20 * 1024 * 1024 and threads > 1:
        logger.info(f"Aktiviran paralelni HTTP Range režim ({threads} niti, ukupno {total_size / (1024 * 1024):.1f} MB)...")
        try:
            with open(temp_path, "wb") as f_alloc:
                f_alloc.truncate(total_size)

            lock = threading.Lock()
            progress = {"downloaded": 0, "last_log_time": time.time(), "last_log_pct": 0}
            start_time = time.time()

            # Podeli u segmente od ~32 MB
            seg_size = 32 * 1024 * 1024
            ranges = []
            offset = 0
            while offset < total_size:
                end = min(offset + seg_size - 1, total_size - 1)
                ranges.append((offset, end))
                offset = end + 1

            def fetch_segment(byte_range):
                b_start, b_end = byte_range
                for seg_attempt in range(1, 4):
                    try:
                        r_req = urllib.request.Request(
                            url,
                            headers={"User-Agent": USER_AGENT, "Range": f"bytes={b_start}-{b_end}"}
                        )
                        with urllib.request.urlopen(r_req, timeout=30) as r_resp, open(temp_path, "r+b") as f_seg:
                            f_seg.seek(b_start)
                            while True:
                                chunk = r_resp.read(1048576)
                                if not chunk:
                                    break
                                f_seg.write(chunk)
                                with lock:
                                    progress["downloaded"] += len(chunk)
                                    now = time.time()
                                    elapsed = max(0.1, now - start_time)
                                    speed_mb = (progress["downloaded"] / (1024 * 1024)) / elapsed
                                    pct = (progress["downloaded"] / total_size * 100)
                                    if cron_mode:
                                        if pct - progress["last_log_pct"] >= 15 or (now - progress["last_log_time"]) >= 15:
                                            dl_mb = progress["downloaded"] / (1024 * 1024)
                                            tot_mb = total_size / (1024 * 1024)
                                            logger.info(f"   Napredak: {pct:.1f}% ({dl_mb:.1f} / {tot_mb:.1f} MB) @ {speed_mb:.1f} MB/s")
                                            progress["last_log_time"] = now
                                            progress["last_log_pct"] = pct
                        return True
                    except Exception as seg_err:
                        if seg_attempt == 3:
                            raise seg_err
                        time.sleep(2 * seg_attempt)
                return False

            with concurrent.futures.ThreadPoolExecutor(max_workers=threads) as pool:
                futures = [pool.submit(fetch_segment, r) for r in ranges]
                for fut in concurrent.futures.as_completed(futures):
                    fut.result()

            if expected_sha256:
                logger.info("Verifikujem SHA256 kontrolnu sumu preuzetog paketa...")
                if verify_file_sha256(temp_path, expected_sha256):
                    logger.info("[SUCCESS] SHA256 heš kontrolna suma je 100% potvrđena.")
                    if os.path.exists(destination_path):
                        os.remove(destination_path)
                    os.rename(temp_path, destination_path)
                    logger.info(f"[SUCCESS] Paket uspešno uskladišten na NAS: {destination_path}")
                    return True
                else:
                    logger.warning("SHA256 u paralelnom režimu se ne poklapa, prelazim na sekvencijalni stream...")
            else:
                if os.path.exists(destination_path):
                    os.remove(destination_path)
                os.rename(temp_path, destination_path)
                logger.info(f"[SUCCESS] Paket uspešno uskladišten na NAS: {destination_path}")
                return True
        except Exception as mt_err:
            logger.warning(f"Paralelno preuzimanje prekinuto ({mt_err}), prelazim na sekvencijalni stream...")

    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})

    max_retries = 3
    for attempt in range(1, max_retries + 1):
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                total_size = int(resp.headers.get("Content-Length", expected_size))
                downloaded = 0
                start_time = time.time()
                last_log_time = start_time
                last_log_pct = 0

                with open(temp_path, "wb") as f_out:
                    chunk_size = 524288  # 512 KB
                    while True:
                        chunk = resp.read(chunk_size)
                        if not chunk:
                            break
                        f_out.write(chunk)
                        downloaded += len(chunk)

                        # Prikaz napretka
                        now = time.time()
                        elapsed = max(0.1, now - start_time)
                        speed_mb = (downloaded / (1024 * 1024)) / elapsed
                        pct = (downloaded / total_size * 100) if total_size > 0 else 0

                        if not cron_mode:
                            bar_len = 25
                            filled = int(bar_len * downloaded / total_size) if total_size > 0 else 0
                            bar = "=" * filled + "-" * (bar_len - filled)
                            dl_mb = downloaded / (1024 * 1024)
                            tot_mb = total_size / (1024 * 1024)
                            msg = f"\r   [{bar}] {pct:5.1f}% ({dl_mb:6.1f} / {tot_mb:6.1f} MB) {speed_mb:5.1f} MB/s"
                            sys.stdout.write(msg)
                            sys.stdout.flush()
                        else:
                            # U cron režimu logujemo na svakih 25% ili svakih 30s
                            if pct - last_log_pct >= 25 or (now - last_log_time) >= 30:
                                dl_mb = downloaded / (1024 * 1024)
                                tot_mb = total_size / (1024 * 1024)
                                logger.info(f"   Napredak: {pct:.1f}% ({dl_mb:.1f} / {tot_mb:.1f} MB) @ {speed_mb:.1f} MB/s")
                                last_log_time = now
                                last_log_pct = pct

                if not cron_mode:
                    sys.stdout.write("\n")
                    sys.stdout.flush()

            # Verifikacija heša preuzetog fajla
            if expected_sha256:
                logger.info("Verifikujem SHA256 kontrolnu sumu preuzetog paketa...")
                if not verify_file_sha256(temp_path, expected_sha256):
                    logger.error(f"SHA256 heš se ne poklapa za {filename}! Uklanjam nevalidan fajl.")
                    if os.path.exists(temp_path):
                        os.remove(temp_path)
                    if attempt < max_retries:
                        logger.info(f"Pokušavam ponovo ({attempt + 1}/{max_retries})...")
                        time.sleep(3)
                        continue
                    return False
                logger.info("[SUCCESS] SHA256 heš kontrolna suma je 100% potvrđena.")

            # Atomično preimenovanje
            if os.path.exists(destination_path):
                os.remove(destination_path)
            os.rename(temp_path, destination_path)
            logger.info(f"[SUCCESS] Paket uspešno uskladišten na NAS: {destination_path}")
            return True

        except Exception as e:
            logger.warning(f"Greška tokom preuzimanja (Pokušaj {attempt}/{max_retries}): {e}")
            if os.path.exists(temp_path):
                try:
                    os.remove(temp_path)
                except Exception:
                    pass
            if attempt < max_retries:
                time.sleep(5 * attempt)
            else:
                logger.error(f"Preuzimanje fajla {filename} nije uspelo nakon {max_retries} pokušaja.")
                return False

    return False


def prune_old_updates(nas_dir, keep=DEFAULT_KEEP, dry_run=False):
    """
    Skenira sve .msu datoteke u nas_dir, sortira ih po datumu izmene,
    i zadržava najnovijih N fajlova po Windows verziji, dok starije uklanja.
    """
    if not os.path.isdir(nas_dir):
        return

    logger.info(f"Proveravam politiku čuvanja verzija (--keep {keep}) u {nas_dir}...")
    msu_files = [
        os.path.join(nas_dir, f) for f in os.listdir(nas_dir)
        if f.lower().endswith(".msu") and os.path.isfile(os.path.join(nas_dir, f))
    ]

    if not msu_files:
        logger.info("Nema postojećih .msu paketa za rotaciju.")
        return

    # Grupisanje fajlova po tipu paketa (.NET vs OS LCU) i Windows verziji / prefiksu
    groups = {}
    for f in msu_files:
        fname = os.path.basename(f)
        if "ndp" in fname.lower():
            grp = "ndp_dotnet"
        else:
            # Detektuj verziju iz imena fajla (npr. windows11.0-kb5134133 ili 23h2/24h2/25h2)
            m_ver = re.search(r'(2[3-6]h\d|windows11\.\d)', fname, re.I)
            grp = m_ver.group(1).lower() if m_ver else "general"
        if grp not in groups:
            groups[grp] = []
        groups[grp].append(f)

    for grp_name, files in groups.items():
        # Sortiraj po mtime opadajuće (najnoviji prvi)
        files.sort(key=lambda p: os.path.getmtime(p), reverse=True)
        if len(files) > keep:
            to_remove = files[keep:]
            logger.info(f"Grupa [{grp_name}]: Pronađeno {len(files)} paketa. Zadržavam {keep} najnovija, uklanjam {len(to_remove)} starijih:")
            for rf in to_remove:
                rf_name = os.path.basename(rf)
                rf_size_mb = os.path.getsize(rf) / (1024 * 1024)
                if dry_run:
                    logger.info(f"   [DRY-RUN] Obrisao bih: {rf_name} ({rf_size_mb:.1f} MB)")
                else:
                    try:
                        os.remove(rf)
                        logger.info(f"   [ROTACIJA] Obrisan stariji paket: {rf_name} ({rf_size_mb:.1f} MB)")
                    except Exception as e:
                        logger.warning(f"   Ne mogu da obrišem {rf_name}: {e}")
        else:
            logger.info(f"Grupa [{grp_name}]: {len(files)} paketa (u granicama --keep {keep}). Nema brisanja.")


def main():
    parser = argparse.ArgumentParser(
        description="Centralizovani NAS Update Sinhronizator za GWADT (Windows 11 LCU Keširanje)"
    )
    parser.add_argument(
        "--nas-dir",
        type=str,
        default="",
        help="Ciljni direktorijum na NAS skladištu za čuvanje .msu fajlova (podrazumevano iz Wifi-Servis.ini)"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Samo provera i ispis dostupnih kumulativnih zakrpi bez preuzimanja"
    )
    parser.add_argument(
        "--cron",
        action="store_true",
        help="Tihi režim za automatsko periodično izvršavanje jednom nedeljno sa logovanjem u fajl"
    )
    parser.add_argument(
        "--keep",
        type=int,
        default=DEFAULT_KEEP,
        help=f"Broj najnovijih verzija zakrpi koje se čuvaju po verziji (podrazumevano {DEFAULT_KEEP})"
    )
    parser.add_argument(
        "--version",
        type=str,
        default="all",
        help="Odabir verzije Windows 11 (23H2, 24H2, 25H2 ili all)"
    )
    parser.add_argument(
        "--log-file",
        type=str,
        default="",
        help="Putanja do log fajla (podrazumevano nas_update_sync.log u ciljnom direktorijumu ili Skripte/)"
    )

    args = parser.parse_args()

    # Određivanje repo root direktorijuma
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)

    # Određivanje NAS direktorijuma
    nas_dir = args.nas_dir.strip() if args.nas_dir else ""
    if not nas_dir:
        ini_nas = load_nas_dir_from_ini(repo_root)
        if ini_nas:
            nas_dir = ini_nas
        else:
            # Podrazumevana fallback lokacija
            default_linux = "/mnt/nas/gwadt-cache/Updates/Win11"
            if sys.platform != "win32" and os.path.isdir("/mnt/nas"):
                nas_dir = default_linux
            else:
                nas_dir = os.path.join(repo_root, "Updates", "Win11")

    # Podešavanje logovanja
    log_file = args.log_file.strip()
    if not log_file:
        if args.cron:
            log_file = os.path.join(script_dir, "nas_update_sync.log")
    setup_logging(log_file=log_file, cron_mode=args.cron)

    logger.info("=" * 70)
    logger.info("GWADT CENTRALIZOVANI NAS UPDATE SINHRONIZATOR")
    logger.info(f"Vreme pokretanja: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    logger.info(f"Ciljni NAS direktorijum: {nas_dir}")
    logger.info(f"Režim rada: {'DRY-RUN' if args.dry_run else ('CRON' if args.cron else 'INTERAKTIVNO')}")
    logger.info(f"Politika čuvanja: {args.keep} najnovije verzije")
    logger.info("=" * 70)

    # Verzije za pretragu
    if args.version.lower() == "23h2":
        ver_list = ("23H2",)
    elif args.version.lower() == "24h2":
        ver_list = ("24H2",)
    elif args.version.lower() == "25h2":
        ver_list = ("25H2",)
    else:
        ver_list = ("25H2", "24H2")

    # 1. Pronalaženje LCU zakrpi na Microsoft Update Catalogu
    lcu_items = find_latest_lcu_updates(windows_versions=ver_list)

    if not lcu_items:
        logger.warning("Nije pronađena nijedna nova kumulativna bezbednosna zakrpa na Microsoft Catalogu.")
        return 0

    logger.info(f"Ukupno identifikovano {len(lcu_items)} LCU paketa za procesiranje.")

    # 2. Dry-Run prikaz ili preuzimanje
    success_count = 0
    for it in lcu_items:
        title = it["title"]
        ver = it["win_version"]
        filename = it["filename"]
        url = it["url"]
        size_bytes = it["size_bytes"]
        sha256 = it["sha256"]

        target_file = os.path.join(nas_dir, filename)

        logger.info("-" * 60)
        logger.info(f"Paket: {title}")
        logger.info(f"Verzija OS: Windows 11 {ver} x64 | KB: {it['kb']}")
        logger.info(f"Fajl: {filename} ({it['size_str']})")
        logger.info(f"URL: {url}")
        logger.info(f"SHA256: {sha256 if sha256 else 'N/A'}")

        if args.dry_run:
            logger.info("   [DRY-RUN] Preuzimanje preskočeno (samo provera).")
            success_count += 1
            continue

        # Preuzimanje i verifikacija
        ok = download_file_stream(
            url=url,
            destination_path=target_file,
            expected_size=size_bytes,
            expected_sha256=sha256,
            cron_mode=args.cron
        )
        if ok:
            success_count += 1

    # 3. Rotacija starih verzija (pruning)
    if os.path.isdir(nas_dir):
        prune_old_updates(nas_dir=nas_dir, keep=args.keep, dry_run=args.dry_run)

    logger.info("=" * 70)
    logger.info(f"Završeno! Uspešno obrađeno: {success_count}/{len(lcu_items)} LCU zakrpi.")
    logger.info("=" * 70)

    return 0 if success_count == len(lcu_items) else 1


if __name__ == "__main__":
    sys.exit(main())
