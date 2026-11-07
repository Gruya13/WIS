"""
servishub.drivers.version
=========================
Parsiranje i robusno poređenje verzija i datuma drajvera.
"""

import re
from datetime import datetime, date


def parse_driver_date(d_val) -> "date | None":
    """
    Parsira datum drajvera u datetime.date objekat.
    Podržava formate:
    - ISO: 'YYYY-MM-DD', 'YYYY-MM-DDTHH:MM:SS'
    - US: 'M/D/YYYY', 'MM/DD/YYYY'
    - EU/Lokalni: 'D/M/YYYY', 'DD/MM/YYYY', 'DD.MM.YYYY.', 'DD.MM.YYYY', 'D.M.YYYY.'
    - WMI/CIM: 'YYYYMMDD...' (npr. '20230515000000.000000-000')
    - datetime ili date objekat
    """
    if not d_val:
        return None
    if isinstance(d_val, datetime):
        return d_val.date()
    if isinstance(d_val, date):
        return d_val

    s = str(d_val).strip()
    if not s or s.lower() in ("unknown", "none", "null", "n/a", "-"):
        return None

    # WMI / CIM datum formata YYYYMMDD...
    wmi_m = re.match(r'^(\d{4})(\d{2})(\d{2})', s)
    if wmi_m and not ("-" in s or "/" in s or "." in s):
        try:
            return date(int(wmi_m.group(1)), int(wmi_m.group(2)), int(wmi_m.group(3)))
        except ValueError:
            pass

    clean_s = s
    if "T" in clean_s:
        clean_s = clean_s.split("T")[0].strip()
    else:
        parts = clean_s.split()
        if len(parts) > 1 and ":" in parts[1]:
            clean_s = parts[0].strip()

    formats = (
        "%Y-%m-%d",
        "%m/%d/%Y",
        "%d/%m/%Y",
        "%m-%d-%Y",
        "%d-%m-%Y",
        "%d.%m.%Y.",
        "%d.%m.%Y",
        "%Y.%m.%d",
        "%Y/%m/%d"
    )
    for fmt in formats:
        try:
            return datetime.strptime(clean_s, fmt).date()
        except ValueError:
            pass

    return None


def parse_driver_version_components(ver_val) -> list:
    """
    Parsira verziju u listu celih brojeva za semantičko poređenje:
    Npr. '10.0.19041.1' -> [10, 0, 19041, 1]
    '6.0.9500.1' -> [6, 0, 9500, 1]
    """
    if not ver_val:
        return []
    s = str(ver_val).strip()
    return [int(x) for x in re.findall(r'\d+', s)]


def is_catalog_driver_newer(curr_ver: str, curr_date: str, cat_ver: str, cat_date: str) -> bool:
    """
    Robusno poredi verziju i datum drajvera sa Microsoft OEM kataloga u odnosu na trenutno instalirani drajver.
    Pravila:
    - Parsira datume u datetime.date objekte.
    - Ako je cat_date > curr_date: vraća True.
    - Ako je cat_date < curr_date: vraća False.
    - Ako su datumi identični ili nepoznati: poredi numeričke komponente semantičke verzije.
      Ako je cat_ver > curr_ver: vraća True.
    - Inače vraća False.
    """
    c_date = parse_driver_date(curr_date)
    k_date = parse_driver_date(cat_date)

    if c_date is not None and k_date is not None:
        if k_date > c_date:
            return True
        elif k_date < c_date:
            return False

    c_ver = parse_driver_version_components(curr_ver)
    k_ver = parse_driver_version_components(cat_ver)

    if k_ver and c_ver:
        if k_ver > c_ver:
            return True
        elif k_ver < c_ver:
            return False
        else:
            return False
    elif k_ver and not c_ver:
        return True

    return False
