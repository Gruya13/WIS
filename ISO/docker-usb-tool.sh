#!/usr/bin/env bash
# ==============================================================================
# GWADT USB Tool - Docker Servis Kontroler
# Upravljanje trajnim, uvek dostupnim Docker kontejnerom za USB održavanje
# ==============================================================================

set -e

# Određivanje lokacije skripte i projekta
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$SCRIPT_DIR/USB Tool/docker-compose.yml" ]]; then
    COMPOSE_DIR="$SCRIPT_DIR/USB Tool"
elif [[ -f "$SCRIPT_DIR/docker-compose.yml" ]]; then
    COMPOSE_DIR="$SCRIPT_DIR"
else
    echo "[GRESKA] docker-compose.yml nije pronađen!"
    exit 1
fi

COMPOSE_FILE="$COMPOSE_DIR/docker-compose.yml"
CONTAINER_NAME="gwadt-usb-tool"
PORT="${GWADT_PORT:-8092}"

# Podrška za opcioni --sudo argument
if [[ "$1" == "--sudo" ]]; then
    USE_SUDO=1
    shift
fi

show_header() {
    echo "================================================================================"
    echo "         GWADT USB TOOL - DOCKER SERVIS KONTROLER (ALWAYS-AVAILABLE)           "
    echo "                  WIS Service Windows 11 Automatizacija                         "
    echo "================================================================================"
}

show_help() {
    show_header
    echo "Upotreba: $0 [--sudo] {start|stop|restart|status|logs|build|shell|help}"
    echo ""
    echo "Komande:"
    echo "  start    - Pokreće USB Tool u trajnom pozadinskom Docker režimu (restart: unless-stopped)"
    echo "  stop     - Zaustavlja USB Tool kontejner"
    echo "  restart  - Restartuje USB Tool kontejner"
    echo "  status   - Prikazuje stanje kontejnera, HTTP odziv i pristupne URL adrese"
    echo "  logs     - Uživo prati rad i logove servisa u konzoli"
    echo "  build    - Ponovo kompajlira/gradi Docker sliku sa najnovijim izmenama"
    echo "  shell    - Otvara bash konzolu unutar kontejnera radi dijagnostike"
    echo "  help     - Prikazuje ovu pomoć"
    echo ""
    echo "Opcije:"
    echo "  --sudo   - Forsira izvršavanje docker komandi preko sudo"
}

if [[ "$1" == "help" || "$1" == "--help" || "$1" == "-h" ]]; then
    show_help
    exit 0
fi

# Detekcija komande za Docker (direktno ili uz sudo ako grupni token još nije osvežen)
DOCKER_CMD="docker"
if ! docker ps >/dev/null 2>&1; then
    if [[ -n "$USE_SUDO" ]]; then
        DOCKER_CMD="sudo docker"
    elif sudo -n docker ps >/dev/null 2>&1; then
        DOCKER_CMD="sudo docker"
    else
        echo "================================================================================"
        echo " [UPOZORENJE] Nemate direktno pravo pristupa Docker socketu (/var/run/docker.sock)."
        echo "              Korisnik '$(whoami)' jeste dodat u 'docker' grupu, ali trenutni shell"
        echo "              proces još uvek nema osvežen grupni token (GID docker grupe)."
        echo "================================================================================"
        echo " Rešenja:"
        echo "  1) Pokrenite komandu uz sudo:     sudo $0 ${1:-start}"
        echo "  2) Ili dodajte --sudo zastavicu:  $0 --sudo ${1:-start}"
        echo "  3) Ili se jednom odjavite i ponovo prijavite na sistem (logout/login)"
        echo "     kako bi docker komanda trajno radila bez sudo lozinke."
        echo "================================================================================"
        exit 1
    fi
fi

# Detekcija Docker Compose komande (v2 plugin ili v1)
if $DOCKER_CMD compose version >/dev/null 2>&1; then
    COMPOSE_CMD="$DOCKER_CMD compose -f \"$COMPOSE_FILE\""
elif command -v docker-compose >/dev/null 2>&1; then
    if [[ "$DOCKER_CMD" == *"sudo"* ]]; then
        COMPOSE_CMD="sudo docker-compose -f \"$COMPOSE_FILE\""
    else
        COMPOSE_CMD="docker-compose -f \"$COMPOSE_FILE\""
    fi
else
    echo "[GRESKA] Docker Compose nije instaliran na sistemu!"
    exit 1
fi

# Funkcija za detekciju lokalnih IP adresa
get_lan_ips() {
    ip -4 -o addr show 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | grep -v '^127\.' || true
}

show_urls() {
    echo ""
    echo " [OK] Pristup kontrolnom panelu:"
    echo "      📍 Lokalna adresa:  http://127.0.0.1:${PORT}"
    for ip in $(get_lan_ips); do
        echo "      🌐 Mrežna adresa:  http://${ip}:${PORT}"
    done
    echo ""
}

case "${1:-status}" in
    start)
        show_header
        echo "[*] Pokrećem GWADT USB Tool kontejner u pozadini..."
        eval $COMPOSE_CMD up -d
        echo "[OK] Servis je uspešno startovan i konfigurisan da UVEK radi u pozadini!"
        show_urls
        ;;

    stop)
        show_header
        echo "[*] Zaustavljam GWADT USB Tool kontejner..."
        eval $COMPOSE_CMD down
        echo "[OK] Servis je bezbedno zaustavljen."
        ;;

    restart)
        show_header
        echo "[*] Restartujem GWADT USB Tool servis..."
        eval $COMPOSE_CMD restart
        echo "[OK] Servis je restartovan."
        show_urls
        ;;

    status)
        show_header
        echo "[*] Status kontejnera ($CONTAINER_NAME):"
        if $DOCKER_CMD ps --filter "name=$CONTAINER_NAME" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}" | grep -q "$CONTAINER_NAME"; then
            $DOCKER_CMD ps --filter "name=$CONTAINER_NAME" --format "table {{.Names}}\t{{.Status}}\t{{.RunningFor}}"
            echo ""
            echo "[*] Provera HTTP odziva servisa na portu ${PORT}..."
            HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:${PORT}/api/status" 2>/dev/null || echo "FAIL")
            if [[ "$HTTP_CODE" == "200" ]]; then
                echo " [OK] Web Dashboard i REST API su AKTIVNI i operativni (HTTP 200 OK)!"
                show_urls
            else
                echo " [UPOZORENJE] Kontejner radi, ali HTTP odziv je: $HTTP_CODE (servis se možda još inicijalizuje)."
            fi
        else
            echo " [INFO] Kontejner '$CONTAINER_NAME' trenutno NIJE pokrenut."
            echo "        Za pokretanje unesite: $0 start"
        fi
        ;;

    logs)
        show_header
        echo "[*] Prikazujem logove servisa ($CONTAINER_NAME)... (Pritisnite Ctrl+C za izlaz)"
        eval $COMPOSE_CMD logs -f --tail=100
        ;;

    build)
        show_header
        echo "[*] Gradim novu verziju Docker slike za GWADT USB Tool..."
        eval $COMPOSE_CMD build --no-cache
        echo "[OK] Docker slika je uspešno napravljena!"
        ;;

    shell)
        echo "[*] Otvaram interaktivni shell unutar kontejnera '$CONTAINER_NAME'..."
        $DOCKER_CMD exec -it "$CONTAINER_NAME" /bin/bash
        ;;

    *)
        show_help
        exit 1
        ;;
esac
