"""
servishub.drivers.scraper
=========================
Scraper za Microsoft Update Catalog (https://www.catalog.update.microsoft.com).
Omogućava pretragu zvaničnih OEM drajvera po HWID-ju, filtriranje za Windows 10/11 x64,
dohvatanje direktnih Microsoft CDN .cab linkova i verifikovano preuzimanje.
"""

import sys
import re
import json
import urllib.request
import urllib.parse
from datetime import datetime
from servishub.drivers.hwid import generate_hwid_candidates


class MsUpdateCatalogScraper:
    """
    Scraper za Microsoft Update Catalog (https://www.catalog.update.microsoft.com).
    Omogućava pretragu zvaničnih OEM drajvera po HWID-ju, filtriranje za Windows 10/11 x64,
    dohvatanje direktnih Microsoft CDN .cab linkova i verifikovano preuzimanje.
    """
    SEARCH_URL = "https://www.catalog.update.microsoft.com/Search.aspx"
    DOWNLOAD_DIALOG_URL = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx"
    DEFAULT_USER_AGENT = (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
    )

    def __init__(self, user_agent: str = None):
        self.user_agent = user_agent or self.DEFAULT_USER_AGENT

    @staticmethod
    def parse_date(date_str: str) -> datetime:
        """Parsira datum iz kataloga (npr. '10/2/2025' ili '2025-10-02')."""
        for fmt in ("%m/%d/%Y", "%Y-%m-%d", "%d/%m/%Y"):
            try:
                return datetime.strptime(date_str.strip(), fmt)
            except ValueError:
                pass
        return datetime.min

    @staticmethod
    def parse_version(ver_str: str) -> tuple:
        """Parsira verziju u tuple celih brojeva radi poređenja (npr. '10.79.50.1003' -> (10, 79, 50, 1003))."""
        try:
            nums = [int(p) for p in re.findall(r'\d+', ver_str)]
            return tuple(nums)
        except Exception:
            return ()

    def search(self, query: str, timeout: int = 15) -> list:
        """
        Pretražuje Microsoft Update Catalog za dati upit (npr. 'PCI\\VEN_10EC&DEV_8168').
        Parsira HTML tabelu i vraća listu pronađenih unosa sa detaljima.
        """
        if not query or not query.strip():
            return []

        clean_query = query.strip()
        url = f"{self.SEARCH_URL}?q={urllib.parse.quote(clean_query)}"
        req = urllib.request.Request(url, headers={"User-Agent": self.user_agent})

        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                html = resp.read().decode("utf-8", errors="ignore")
        except Exception as e:
            print(f"[WARN] [MsCatalog] Neuspeh HTTP upita za '{clean_query}': {e}", file=sys.stderr)
            return []

        # Parsiranje tabele sa rezultatima: redovi <tr id="{guid}_R{idx}" ...>
        rows = re.findall(r'<tr id=\"([^\"]+)\"[^>]*>(.*?)</tr>', html, re.DOTALL)
        entries = []
        for row_id, row_content in rows:
            tds = re.findall(r'<td[^>]*>(.*?)</td>', row_content, re.DOTALL)
            if len(tds) < 7:
                continue

            guid = row_id.split("_")[0]

            # TD 1: Naslov
            title_m = re.search(r'>([^<]+)</a>', tds[1])
            title = title_m.group(1).strip() if title_m else re.sub(r'<[^>]+>', ' ', tds[1]).strip()
            title = " ".join(title.split())

            # TD 2: Proizvodi / OS podrška
            products = " ".join(re.sub(r'<[^>]+>', ' ', tds[2]).split())

            # TD 3: Klasa hardvera
            dev_class = " ".join(re.sub(r'<[^>]+>', ' ', tds[3]).split())

            # TD 4: Datum ažuriranja
            date_str = " ".join(re.sub(r'<[^>]+>', ' ', tds[4]).split())
            dt = self.parse_date(date_str)

            # TD 5: Verzija
            ver_str = " ".join(re.sub(r'<[^>]+>', ' ', tds[5]).split())
            v_parsed = self.parse_version(ver_str)

            # TD 6: Veličina
            size_str = " ".join(re.sub(r'<[^>]+>', ' ', tds[6]).split())
            size_bytes = 0
            sz_m = re.search(r'id=\"[^\"]+_originalSize\"[^>]*>(\d+)<', tds[6])
            if sz_m:
                try:
                    size_bytes = int(sz_m.group(1))
                except ValueError:
                    size_bytes = 0

            entries.append({
                "update_id": guid,
                "title": title,
                "products": products,
                "device_class": dev_class,
                "date_str": date_str,
                "date": dt,
                "version": ver_str,
                "version_parsed": v_parsed,
                "size_str": size_str,
                "size_bytes": size_bytes
            })

        return entries

    def filter_for_win_x64(self, entries: list) -> list:
        """
        Filtrira unose tako da zadrži one koji podržavaju 64-bitni Windows 10 ili Windows 11.
        Sortira ih tako da najnoviji datum drajvera sa najvišom verzijom bude prvi.
        """
        filtered = []
        excluded_arch = {"arm64", "arm", "itanium", "ia64", "x86", "32-bit", "32bit"}

        for e in entries:
            combined = (e["products"] + " " + e["title"]).lower()

            if any(arch in combined for arch in excluded_arch):
                continue

            is_win10_11 = any(k in combined for k in ("windows 11", "win 11", "windows 10", "win 10", "windows - client"))
            if not is_win10_11 and "windows" in combined:
                if not any(old in combined for old in ("windows xp", "windows 7", "windows 8", "server 2003", "server 2008")):
                    is_win10_11 = True

            if is_win10_11:
                filtered.append(e)

        filtered.sort(key=lambda x: (x["date"], x["version_parsed"], x["size_bytes"]), reverse=True)
        return filtered

    def get_download_urls(self, update_id: str, timeout: int = 15) -> list:
        """
        Dohvata direktne .cab linkove za preuzimanje preko DownloadDialog.aspx.
        """
        if not update_id:
            return []

        post_data = urllib.parse.urlencode({
            "updateIDs": json.dumps([{"size": 0, "languages": "", "uidInfo": update_id, "updateID": update_id}])
        }).encode("utf-8")

        req = urllib.request.Request(self.DOWNLOAD_DIALOG_URL, data=post_data, headers={"User-Agent": self.user_agent})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                dlg_html = resp.read().decode("utf-8", errors="ignore")
        except Exception as e:
            print(f"[WARN] [MsCatalog] Neuspeh dohvatanja download dijaloga za {update_id}: {e}", file=sys.stderr)
            return []

        links = re.findall(r'downloadInformation\[\d+\]\.files\[\d+\]\.url\s*=\s*[\'\"]([^\'\"]+)[\'\"]', dlg_html)
        if not links:
            links = re.findall(r'https?://[^\s\'\"<>]+\.(?:msu|cab)', dlg_html, re.IGNORECASE)

        unique_links = []
        for l in links:
            if l not in unique_links:
                unique_links.append(l)

        unique_links.sort(key=lambda u: (0 if u.startswith("https://") else 1))
        return unique_links

    def download_cab(self, cab_url: str, timeout: int = 60) -> bytes:
        """
        Preuzima .cab arhivu sa Microsoft CDN-a i verifikuje MSCF zaglavlje.
        """
        if not cab_url:
            raise ValueError("URL za preuzimanje drajvera je prazan")

        req = urllib.request.Request(cab_url, headers={"User-Agent": self.user_agent})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = resp.read()

        if len(data) < 4 or data[:4] != b"MSCF":
            raise ValueError(f"Preuzeta datoteka ne sadrži validan Microsoft Cabinet potpis (MSCF). Veličina: {len(data)} B")

        return data

    def resolve_driver(self, hwid: str) -> dict | None:
        """
        Izvršava kompletnu rezoluciju drajvera:
        1. Generiše HWID kandidate (od specifičnog do generičkog).
        2. Pretražuje Microsoft Catalog.
        3. Filtrira za Windows 10/11 x64 i bira najnoviji.
        4. Dohvata direktan download link.
        Vraća rečnik sa podacima i download URL-om ili None.
        """
        candidates = generate_hwid_candidates(hwid)
        for cand in candidates:
            entries = self.search(cand)
            if not entries:
                continue

            filtered = self.filter_for_win_x64(entries)
            if not filtered:
                continue

            best = filtered[0]
            urls = self.get_download_urls(best["update_id"])
            if urls:
                best["download_url"] = urls[0]
                best["download_urls"] = urls
                best["matched_query"] = cand
                return best

        return None
