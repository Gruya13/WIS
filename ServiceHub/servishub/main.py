"""
servishub.main
==============
FastAPI aplikacija i API ruter ulazna tačka za ServisHub.
Omogućava pokretanje ServisHub API servisa putem ASGI servera (npr. uvicorn).
"""

from servishub.api.updates import router as updates_router
from servishub.version import VERSION

try:
    from fastapi import FastAPI
    from fastapi.middleware.cors import CORSMiddleware

    app = FastAPI(
        title="Tehno-Kutak Centralni Servisni Hub API",
        version=VERSION,
        description="REST API interfejs za ServisHub: telemetrija, zakrpe, drajveri i servisni kartoni.",
    )

    app.add_middleware(
        CORSMiddleware,
        allow_origins=["*"],
        allow_credentials=True,
        allow_methods=["*"],
        allow_headers=["*"],
    )

    # Registracija rutera
    if updates_router is not None:
        app.include_router(updates_router)

    @app.get("/api/health")
    def health():
        return {"status": "ok", "version": VERSION}

    @app.get("/api/status")
    def status():
        return {"status": "ok", "version": VERSION}

except ImportError:
    app = None
