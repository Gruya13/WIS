"""
servishub.api.updates
=====================
REST API ruter i logistika za inspekciju i status skladišta Windows zakrpi (.msu / .cab).
"""

import os
import re
from datetime import datetime
from typing import List, Optional

from servishub.models.updates import UpdatePackageItem, UpdatesStatusResponse


def extract_kb(filename: str) -> Optional[str]:
    """
    Ekstraktuje Microsoft KB broj (npr. 'KB5129195' ili 'KB5043080') iz naziva datoteke.
    Prepoznaje formate:
    - Windows11.0-KB5043080-x64.msu -> KB5043080
    - windows11.0-kb5129195-x64.msu -> KB5129195
    - KB5129242-amd64.cab -> KB5129242
    - windows11.0-kb5043080-x64_953449672073f8fb99badb4cc6d5d7849b9c83e8.msu -> KB5043080
    """
    m = re.search(r'(?i)(?:^|[^a-zA-Z0-9])(KB\d{5,8})(?:[^a-zA-Z0-9]|$)', filename)
    if m:
        return m.group(1).upper()
    return None


def resolve_updates_directory(custom_dir: Optional[str] = None) -> str:
    """
    Određuje putanju skladišta zakrpi u sledećem redosledu:
    1. Eksplicitno prosleđeni direktorijum
    2. SERVIS_UPDATES_DIR iz okruženja
    3. SERVIS_HUB_UPDATES_DIR iz okruženja
    4. active_config.updates_dir (ako je definisan)
    5. /home/gru/Storage/Updates/Win11 (ako postoji)
    6. ./Updates/Win11 (ako postoji)
    7. Fallback na /home/gru/Storage/Updates/Win11
    """
    if custom_dir:
        return os.path.abspath(custom_dir)

    env_dir = os.environ.get("SERVIS_UPDATES_DIR") or os.environ.get("SERVIS_HUB_UPDATES_DIR")
    if env_dir:
        return os.path.abspath(env_dir)

    try:
        from servishub.config import active_config
        if getattr(active_config, "updates_dir", None):
            return active_config.updates_dir
    except Exception:
        pass

    if os.path.isdir("/home/gru/Storage/Updates/Win11"):
        return "/home/gru/Storage/Updates/Win11"

    local_fallback = os.path.abspath(os.path.join(os.getcwd(), "Updates", "Win11"))
    if os.path.isdir(local_fallback):
        return local_fallback

    return "/home/gru/Storage/Updates/Win11"


def scan_updates_directory(directory: Optional[str] = None) -> UpdatesStatusResponse:
    """
    Skenira skladište zakrpi i vraća strukturiran izveštaj o pronađenim .msu i .cab paketima.
    """
    target_dir = resolve_updates_directory(directory)

    if not os.path.isdir(target_dir):
        return UpdatesStatusResponse(
            enabled=True,
            available=False,
            directory=target_dir,
            total_packages=0,
            total_size_bytes=0,
            total_size_mb=0.0,
            kb_list=[],
            packages=[],
        )

    packages: List[UpdatePackageItem] = []
    ignored_extensions = {".part", ".tmp", ".crdownload", ".aria2"}

    try:
        entries = sorted(os.listdir(target_dir))
    except Exception:
        return UpdatesStatusResponse(
            enabled=True,
            available=False,
            directory=target_dir,
            total_packages=0,
            total_size_bytes=0,
            total_size_mb=0.0,
            kb_list=[],
            packages=[],
        )

    for entry in entries:
        lower_entry = entry.lower()
        # Ignoriši privremene/nezavršene datoteke
        if any(lower_entry.endswith(ext) for ext in ignored_extensions):
            continue

        if not (lower_entry.endswith(".msu") or lower_entry.endswith(".cab")):
            continue

        full_path = os.path.join(target_dir, entry)
        if not os.path.isfile(full_path):
            continue

        try:
            stat_res = os.stat(full_path)
            size_bytes = stat_res.st_size
            size_mb = round(size_bytes / (1024 * 1024), 2)
            mtime = stat_res.st_mtime
            modified_at = datetime.fromtimestamp(mtime).isoformat()
            file_type = "msu" if lower_entry.endswith(".msu") else "cab"
            kb = extract_kb(entry)

            pkg = UpdatePackageItem(
                filename=entry,
                kb=kb,
                file_type=file_type,
                size_bytes=size_bytes,
                size_mb=size_mb,
                modified_at=modified_at,
                modified_timestamp=mtime,
                path=full_path,
            )
            packages.append(pkg)
        except Exception:
            continue

    # Sortiraj pakete: najnoviji po mtime prvo, pa abecedno po nazivu
    packages.sort(key=lambda p: (p.modified_timestamp, p.filename), reverse=True)

    total_bytes = sum(p.size_bytes for p in packages)
    total_mb = round(total_bytes / (1024 * 1024), 2)
    kb_list = sorted(list({p.kb for p in packages if p.kb}))

    return UpdatesStatusResponse(
        enabled=True,
        available=True,
        directory=target_dir,
        total_packages=len(packages),
        total_size_bytes=total_bytes,
        total_size_mb=total_mb,
        kb_list=kb_list,
        packages=packages,
    )


# FastAPI ruter integracija
try:
    from fastapi import APIRouter, Query

    router = APIRouter(prefix="/api/updates", tags=["updates"])

    @router.get("/status", response_model=UpdatesStatusResponse)
    def get_updates_status(
        directory: Optional[str] = Query(
            None,
            description="Opciona specifična putanja do direktorijuma sa zakrpama",
        )
    ) -> UpdatesStatusResponse:
        """
        Pregleda konfigurisanu putanju za skladište zakrpi (npr. SERVIS_UPDATES_DIR),
        parsira pronađene .msu i .cab fajlove, izvlači KB brojeve i metapodatke,
        i vraća status dostupnosti NAS keša zakrpi.
        """
        return scan_updates_directory(directory)

except ImportError:
    router = None
