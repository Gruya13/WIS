FROM python:3.11-slim

RUN DEBIAN_FRONTEND=noninteractive apt-get update && apt-get install -y --no-install-recommends \
    openssl \
    libarchive-tools \
    p7zip-full \
    cabextract \
    msitools \
    util-linux \
    parted \
    gdisk \
    dosfstools \
    ntfs-3g \
    udev \
    tzdata \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=Europe/Belgrade
RUN ln -snf /usr/share/zoneinfo/$TZ /etc/localtime && echo $TZ > /etc/timezone

WORKDIR /app

# Osnovna struktura direktorijuma
RUN mkdir -p /app/servishub /app/storage/cache /app/storage/drivers_cache /app/storage/driver_pool /app/storage/wis /app/storage/updates/Win11 /app/harvested/Archives /app/harvested/Pool /app/oem_dropin /app/Logs/reports /app/Logs/live

# Kopiranje izvornog koda ServisHub-a
COPY servishub/ /app/servishub/
COPY hub_server.py hub_cli.py trusted_public_key.xml /app/

ENV PYTHONUNBUFFERED=1
ENV SERVIS_HUB_PORT=8080
ENV SERVIS_HUB_UDP_PORT=8888
ENV SERVIS_UPDATES_DIR=/app/storage/updates/Win11
ENV SERVIS_MODEL_PACKS_DIR=/app/harvested/Archives
ENV SERVIS_OEM_DROPIN_DIR=/app/oem_dropin

EXPOSE 8080 8888/udp

CMD ["python3", "hub_server.py", "--host", "0.0.0.0", "--port", "8080", "--udp-port", "8888", "--root", "/app"]
