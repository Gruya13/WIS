# ==============================================================================
# GWADT USB Tool - Centar za Održavanje i Nadogradnju USB Fleša
# Dockerfile za autonomni, uvek dostupni pozadinski servis
# ==============================================================================

FROM python:3.12-slim-bookworm

# Sprečavanje interaktivnih promptova pri instalaciji paketa
ENV DEBIAN_FRONTEND=noninteractive
ENV PYTHONUNBUFFERED=1

# Instalacija neophodnih sistemskih alata za upravljanje diskovima, WIM i ISO slikama
RUN apt-get update && apt-get install -y --no-install-recommends \
    wimtools \
    util-linux \
    ntfs-3g \
    dosfstools \
    xorriso \
    cabextract \
    aria2 \
    curl \
    dbus \
    libglib2.0-bin \
    procps \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Postavljanje radnog direktorijuma
WORKDIR /app

# Kopiranje aplikativnih modula i GUI datoteka
COPY app.py /app/app.py
COPY core/ /app/core/
COPY gui/ /app/gui/

# Podrazumevane konfiguracione varijable unutar kontejnera
ENV GWADT_PROJECT_ROOT=/project
ENV GWADT_STAGING_DIR=/staging
ENV GWADT_HOST_DOWNLOADS=/host_downloads
ENV GWADT_HOST_STORAGE=/host_storage
ENV GWADT_PORT=8092

EXPOSE 8092

# Pokretanje servera na svim interfejsima bez pokušaja otvaranja GUI browsera u kontejneru
CMD ["python3", "-u", "app.py", "--host", "0.0.0.0", "--port", "8092", "--no-browser"]
