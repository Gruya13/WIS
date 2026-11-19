"""
tests.test_udp_discovery
========================
Verifikacija UDP Auto-Discovery protokola (servishub.discovery).
"""

import time
import socket
import unittest
from servishub.discovery import UdpDiscoveryServer


class TestUdpDiscovery(unittest.TestCase):

    def test_udp_discovery_exchange(self):
        # Pokrećemo server na test UDP portu
        test_udp_port = 18888
        http_test_port = 8080
        srv = UdpDiscoveryServer(http_port=http_test_port, udp_port=test_udp_port)
        srv.start()
        time.sleep(0.05)

        client_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        client_sock.settimeout(2.0)

        try:
            # 1. Test sa tradicionalnim GWADT_DISCOVER paketom
            msg1 = b"GWADT_DISCOVER client=TEST-PC"
            client_sock.sendto(msg1, ("127.0.0.1", test_udp_port))
            data1, _ = client_sock.recvfrom(1024)
            resp1 = data1.decode("utf-8")
            self.assertIn("GWADT_HUB_OFFER", resp1)
            self.assertIn("port=8080", resp1)

            # 2. Test sa novim SERVIS_DISCOVER paketom
            msg2 = b"SERVIS_DISCOVER client=TEST-PC"
            client_sock.sendto(msg2, ("127.0.0.1", test_udp_port))
            data2, _ = client_sock.recvfrom(1024)
            resp2 = data2.decode("utf-8")
            self.assertIn("GWADT_HUB_OFFER", resp2)
            self.assertIn("port=8080", resp2)
        finally:
            client_sock.close()
            srv.running = False


if __name__ == "__main__":
    unittest.main()
