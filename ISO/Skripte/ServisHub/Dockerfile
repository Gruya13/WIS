FROM python:3.11-slim

WORKDIR /app

# Osnovna struktura direktorijuma
RUN mkdir -p /app/Skripte/ServisHub /app/storage/cache /app/Logs/reports /app/Logs/live

# Kopiranje ServisHub skripti u imidž
COPY hub_server.py hub_cli.py /app/Skripte/ServisHub/

EXPOSE 8080 8888/udp

CMD ["python3", "Skripte/ServisHub/hub_server.py", "--host", "0.0.0.0", "--port", "8080", "--udp-port", "8888", "--root", "/app"]
