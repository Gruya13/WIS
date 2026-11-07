"""
tests.test_tui_cli
==================
Unit i integracioni testovi za konzolni TUI Monitor (servishub.tui_cli).
Testira formatiranje AGY servisiranih uređaja, OEM garancije i CLI tokove.
"""

import io
import sys
import unittest
from unittest.mock import patch, MagicMock

from servishub.tui_cli import (
    C_CYAN,
    C_RED,
    C_GREEN,
    C_YELLOW,
    C_BOLD,
    get_status_color,
    extract_warranty,
    format_warranty,
    extract_intervention_text,
    show_fleet_table,
    show_device_details,
    main,
)


class TestTuiCli(unittest.TestCase):

    def test_01_get_status_color(self):
        """Proverava tačnost dodeljivanja ANSI boja po statusima."""
        # AGY statusi: Cijan za Servisiran, Crveno za Kvar
        col_servisiran = get_status_color("Servisiran (AGY)")
        self.assertIn(C_CYAN, col_servisiran)

        col_kvar = get_status_color("Kvar (AGY)")
        self.assertIn(C_RED, col_kvar)

        # Standardni statusi
        self.assertEqual(get_status_color("Aktivno"), C_GREEN)
        self.assertEqual(get_status_color("Restart u toku"), C_YELLOW)
        self.assertEqual(get_status_color("Zavrseno"), C_CYAN)

    def test_02_format_warranty_active(self):
        """Proverava formatiranje aktivne fabričke garancije."""
        w1 = {
            "in_warranty": True,
            "service_level": "Premier Onsite",
            "days_remaining": 448,
        }
        res1 = format_warranty(w1)
        self.assertEqual(res1, "Garancija: Premier Onsite (448d)")

        # Uklanjanje sufiksa 'Support'
        w2 = {
            "in_warranty": True,
            "service_level": "Premier Onsite Support",
            "days_remaining": 120,
        }
        res2 = format_warranty(w2)
        self.assertEqual(res2, "Garancija: Premier Onsite (120d)")

        # Drugi nivo garancije
        w3 = {
            "in_warranty": True,
            "service_level": "Depot Warranty",
            "days_remaining": 365,
        }
        res3 = format_warranty(w3)
        self.assertEqual(res3, "Garancija: Depot Warranty (365d)")

    def test_03_format_warranty_expired_and_empty(self):
        """Proverava formatiranje istekle i nedostajuće garancije."""
        # Istekla preko in_warranty = False
        w_exp1 = {"in_warranty": False, "days_remaining": 0}
        self.assertEqual(format_warranty(w_exp1), "Garancija: Istekla / Van garancije")

        # Istekla preko status_label
        w_exp2 = {"status_label": "Istekla garancija"}
        self.assertEqual(format_warranty(w_exp2), "Garancija: Istekla / Van garancije")

        # Istekla preko fallback statusa
        w_exp3 = {"status": "fallback", "in_warranty": False}
        self.assertEqual(format_warranty(w_exp3), "Garancija: Istekla / Van garancije")

        # Prazan unos
        self.assertEqual(format_warranty(None), "")
        self.assertEqual(format_warranty({}), "")

    def test_04_extract_warranty_sources(self):
        """Proverava dohvatanje garancije iz različitih nivoa telemetrije."""
        # 1. Top level
        d1 = {"warranty": {"in_warranty": True, "service_level": "Premier Onsite", "days_remaining": 200}}
        self.assertIsNotNone(extract_warranty(d1))
        self.assertEqual(extract_warranty(d1)["days_remaining"], 200)

        # 2. latest_report
        d2 = {"latest_report": {"warranty": {"in_warranty": True, "days_remaining": 100}}}
        self.assertEqual(extract_warranty(d2)["days_remaining"], 100)

        # 3. latest_agy_session
        d3 = {"latest_agy_session": {"warranty": {"in_warranty": False}}}
        self.assertFalse(extract_warranty(d3)["in_warranty"])

        # 4. hw
        d4 = {"hw": {"warranty": {"in_warranty": True, "days_remaining": 50}}}
        self.assertEqual(extract_warranty(d4)["days_remaining"], 50)

        # 5. oem u latest_report
        d5 = {"latest_report": {"oem": {"warranty": {"in_warranty": True, "days_remaining": 10}}}}
        self.assertEqual(extract_warranty(d5)["days_remaining"], 10)

        # 6. Nepostojeća garancija
        self.assertIsNone(extract_warranty({}))

    def test_05_extract_intervention_text(self):
        """Proverava ekstrakciju sažetka intervencije za AGY uređaje."""
        # 1. Direktno summary polje
        d1 = {"summary": "Zamenjena termalna pasta i očišćen kuler."}
        self.assertEqual(extract_intervention_text(d1), "Zamenjena termalna pasta i očišćen kuler.")

        # 2. U latest_report
        d2 = {"latest_report": {"summary": "Root cause: Otkaz NVMe SSD-a."}}
        self.assertEqual(extract_intervention_text(d2), "Root cause: Otkaz NVMe SSD-a.")

        # 3. Iz dnevnik_entry
        d3 = {"dnevnik_entry": "### [2026-10-09] - Zamena ekrana\n- Uspešno zamenjen panel."}
        self.assertIn("Zamena ekrana", extract_intervention_text(d3))

        # 4. Fallback za AGY
        d4 = {"status_label": "Servisiran (AGY)"}
        self.assertEqual(extract_intervention_text(d4), "AGY Servis")

    @patch("servishub.tui_cli.clear_screen")
    @patch("servishub.tui_cli.fetch_json")
    def test_06_show_fleet_table_renders_agy_and_warranty(self, mock_fetch, mock_clear):
        """Integracioni test prikaza tabele flote sa AGY uređajima i garancijom."""
        mock_fetch.return_value = {
            "dev_gwadt": {
                "computer_name": "TK-DESKTOP-01",
                "client_ip": "192.168.1.101",
                "phase": 2,
                "phase_name": "Sistem",
                "worker_progress": 45,
                "status_label": "Aktivno",
                "last_seen_sec_ago": 2,
                "hw": {"manufacturer": "ASUS", "model": "ZenBook", "serial": "SN1001"},
            },
            "dev_agy_ok": {
                "computer_name": "TK-LAPTOP-PF123",
                "client_ip": "192.168.1.102",
                "status_label": "Servisiran (AGY)",
                "summary": "Ugrađen nov Kingston 1TB NVMe. Kloniran sistem.",
                "last_seen_sec_ago": 1,
                "hw": {"manufacturer": "Lenovo", "model": "ThinkPad T14", "serial": "PF123456"},
                "warranty": {
                    "in_warranty": True,
                    "service_level": "Premier Onsite",
                    "days_remaining": 815,
                },
            },
            "dev_agy_err": {
                "computer_name": "TK-LAPTOP-8G7X",
                "client_ip": "192.168.1.103",
                "status_label": "Kvar (AGY)",
                "summary": "Root cause: Otkaz matične ploče (kratak spoj).",
                "last_seen_sec_ago": 5,
                "hw": {"manufacturer": "Dell", "model": "Latitude 5420", "serial": "8G7X123"},
                "warranty": {
                    "in_warranty": False,
                    "status_label": "Istekla garancija",
                    "days_remaining": 0,
                },
            },
        }

        out_capture = io.StringIO()
        with patch("sys.stdout", out_capture):
            show_fleet_table(loop=False)

        output = out_capture.getvalue()

        # 1. Provera naslova
        self.assertIn("TEHNO-KUTAK SERVIS-HUB FLEET MONITOR", output)

        # 2. Provera GWADT uređaja
        self.assertIn("ASUS ZenBook", output)
        self.assertIn("F2. Sistem", output)
        self.assertIn("45%", output)

        # 3. Provera AGY servisiranog uređaja
        self.assertIn("Servisiran (AGY)", output)
        self.assertIn("Lenovo ThinkPad T14", output)
        self.assertIn("Garancija: Premier Onsite (815d)", output)
        self.assertIn("Ugrađen nov Kingston", output)

        # 4. Provera AGY kvara
        self.assertIn("Kvar (AGY)", output)
        self.assertIn("Dell Latitude 5420", output)
        self.assertIn("Garancija: Istekla / Van garancije", output)
        self.assertIn("Otkaz matične ploče", output)

    @patch("servishub.tui_cli.fetch_json")
    def test_07_show_device_details(self, mock_fetch):
        """Proverava ispis pojedinačnih detalja uređaja sa garancijom i hardverom."""
        mock_fetch.return_value = {
            "dev_01": {
                "computer_name": "TK-LAPTOP-PF123",
                "device_tag": "Lenovo_ThinkPad_T14_PF123456",
                "client_ip": "192.168.1.50",
                "status_label": "Servisiran (AGY)",
                "summary": "Ugrađen nov SSD disk.",
                "hw": {
                    "manufacturer": "Lenovo",
                    "model": "ThinkPad T14",
                    "serial": "PF123456",
                    "cpu": "Intel Core i7-1185G7",
                    "ram_gb": 16.0,
                },
                "warranty": {
                    "in_warranty": True,
                    "service_level": "Premier Onsite",
                    "days_remaining": 448,
                    "start_date": "2024-01-01",
                    "end_date": "2026-12-31",
                },
            }
        }

        out_capture = io.StringIO()
        with patch("sys.stdout", out_capture):
            found = show_device_details("PF123456")

        self.assertTrue(found)
        output = out_capture.getvalue()
        self.assertIn("DETALJI UREĐAJA", output)
        self.assertIn("Lenovo ThinkPad T14", output)
        self.assertIn("PF123456", output)
        self.assertIn("Servisiran (AGY)", output)
        self.assertIn("Garancija: Premier Onsite (448d)", output)
        self.assertIn("2024-01-01 do 2026-12-31", output)
        self.assertIn("Intel Core i7-1185G7", output)
        self.assertIn("Ugrađen nov SSD disk.", output)

    @patch("servishub.tui_cli.show_fleet_table")
    @patch("servishub.tui_cli.show_device_details")
    @patch("servishub.tui_cli.watch_device_log")
    def test_08_cli_main_dispatch(self, mock_watch, mock_details, mock_table):
        """Proverava CLI argumente za --watch, --device i --once."""
        from servishub.tui_cli import DEFAULT_HUB_URL
        # 1. --watch
        with patch("sys.argv", ["hub_cli.py", "--watch", "TK-LAPTOP-01"]):
            main()
            mock_watch.assert_called_once_with("TK-LAPTOP-01", hub_url=DEFAULT_HUB_URL)

        # 2. --device
        with patch("sys.argv", ["hub_cli.py", "--device", "TK-LAPTOP-01"]):
            main()
            mock_details.assert_called_once_with("TK-LAPTOP-01", hub_url=DEFAULT_HUB_URL)

        # 3. --once
        with patch("sys.argv", ["hub_cli.py", "--once"]):
            main()
            mock_table.assert_called_once_with(hub_url=DEFAULT_HUB_URL, loop=False)


if __name__ == "__main__":
    unittest.main()
