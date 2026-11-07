FROM python:3.11-slim

WORKDIR /app

RUN mkdir -p /app/servishub /app/storage/cache /app/storage/drivers_cache /app/storage/wis /app/Logs/reports /app/Logs/live

COPY servishub/ /app/servishub/
COPY hub_server.py hub_cli.py /app/

ENV PYTHONUNBUFFERED=1
ENV SERVIS_HUB_PORT=8080
ENV SERVIS_HUB_UDP_PORT=8888

EXPOSE 8080 8888/udp

CMD ["python3", "hub_server.py", "--host", "0.0.0.0", "--port", "8080", "--udp-port", "8888", "--root", "/app"]
