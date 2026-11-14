"""
servishub.api
=============
REST API rute i podsistemi za ServisHub.
"""

from servishub.api.updates import (
    extract_kb,
    extract_os_build,
    scan_updates_directory,
    resolve_updates_directory,
    find_update_package,
    save_uploaded_update_package,
    save_uploaded_update_package_stream,
    cache_kb_from_ms_catalog,
    prune_updates_cache,
    sanitize_model_pack_name,
    resolve_model_packs_directory,
    get_model_pack_status,
    save_uploaded_model_pack_stream,
    router as updates_router,
)

__all__ = [
    "extract_kb",
    "extract_os_build",
    "scan_updates_directory",
    "resolve_updates_directory",
    "find_update_package",
    "save_uploaded_update_package",
    "save_uploaded_update_package_stream",
    "cache_kb_from_ms_catalog",
    "prune_updates_cache",
    "sanitize_model_pack_name",
    "resolve_model_packs_directory",
    "get_model_pack_status",
    "save_uploaded_model_pack_stream",
    "updates_router",
]
