"""
servishub.discovery
===================
UDP Auto-Discovery responder na portu 8888.
Osluškivač odgovara na GWADT_DISCOVER i SERVIS_DISCOVER upite klijenata,
omogućavajući Zero-Config automatsko pronalaženje ServisHub-a na lokalnoj mreži.
"""

import sys
import socket
import select
import threading


class UdpDiscoveryServer(threading.Thread):
    """
    Pozadinska nit koja osluškuje UDP broadcast zahteve i šalje ponudu sa HTTP portom.
    """

    def __init__(self, http_port: int, udp_port: int = 8888):
        super().__init__(daemon=True, name="UDP-Discovery")
        self.http_port = http_port
        self.udp_port = udp_port
        self.running = True

    def run(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            sock.bind(("0.0.0.0", self.udp_port))
            print(f"[*] UDP Auto-Discovery osluškuje na portu {self.udp_port}...")
            while self.running:
                r, _, _ = select.select([sock], [], [], 2.0)
                if not r:
                    continue
                data, addr = sock.recvfrom(2048)
                msg = data.decode("utf-8", errors="ignore").strip()
                if "GWADT_DISCOVER" in msg or "SERVIS_DISCOVER" in msg:
                    resp = f"GWADT_HUB_OFFER port={self.http_port} name=WISService-Hub"
                    sock.sendto(resp.encode("utf-8"), addr)
        except Exception as e:
            if self.running:
                print(f"[WARN] UDP Discovery greška: {e}", file=sys.stderr)
        finally:
            try:
                sock.close()
            except Exception:
                pass
