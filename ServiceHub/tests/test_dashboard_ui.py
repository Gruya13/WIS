"""
tests.test_dashboard_ui
=======================
Testovi za verifikaciju Web Dashboard interfejsa (HTML5/CSS3/Vanilla JS).
Proverava Tab Navigaciju, WIS Builder, Win11 Zakrpe, OEM/Garanciju, AGY podršku i modale.
"""

import unittest
from servishub.web.dashboard import HTML_DASHBOARD
from servishub.version import VERSION


class TestDashboardUI(unittest.TestCase):
    """Testovi za HTML_DASHBOARD konstantu."""

    def test_01_doctype_and_title(self):
        self.assertIn("<!DOCTYPE html>", HTML_DASHBOARD)
        self.assertIn("<title>WIS Service | ServisHub Centralni Sistem</title>", HTML_DASHBOARD)
        self.assertIn("ServisHub v" + VERSION, HTML_DASHBOARD)

    def test_02_tab_navigation_structure(self):
        # 3 taba u navigaciji
        self.assertIn('id="tab-btn-fleet"', HTML_DASHBOARD)
        self.assertIn('id="tab-btn-wis"', HTML_DASHBOARD)
        self.assertIn('id="tab-btn-updates"', HTML_DASHBOARD)
        self.assertIn("Radionička Flota & Telemetrija", HTML_DASHBOARD)
        self.assertIn("WIS Centar (ISO & USB Builder)", HTML_DASHBOARD)
        self.assertIn("Windows 11 Zakrpe", HTML_DASHBOARD)

        # 3 sekcije sadržaja tabova
        self.assertIn('id="tab-fleet"', HTML_DASHBOARD)
        self.assertIn('id="tab-wis"', HTML_DASHBOARD)
        self.assertIn('id="tab-updates"', HTML_DASHBOARD)

        # switchTab funkcija
        self.assertIn("function switchTab(tabId)", HTML_DASHBOARD)

    def test_03_fleet_telemetry_and_badges(self):
        # Osnovni fleet elementi
        self.assertIn('id="fleet-grid"', HTML_DASHBOARD)
        self.assertIn('id="stat-total"', HTML_DASHBOARD)
        self.assertIn('id="stat-active"', HTML_DASHBOARD)
        self.assertIn('id="stat-done"', HTML_DASHBOARD)

        # OEM & Garancija bedževi
        self.assertIn("Premier Onsite", HTML_DASHBOARD)
        self.assertIn("U Garanciji", HTML_DASHBOARD)
        self.assertIn("Van Garancije", HTML_DASHBOARD)

        # Izvor drajvera bedževi
        self.assertIn("Lenovo SCCM ZFS Keš", HTML_DASHBOARD)
        self.assertIn("Microsoft Catalog", HTML_DASHBOARD)

        # AGY podrška
        self.assertIn("Servisiran (AGY)", HTML_DASHBOARD)
        self.assertIn("Kvar (AGY)", HTML_DASHBOARD)
        self.assertIn("Sažetak AGY Dijagnostike / Intervencije", HTML_DASHBOARD)
        self.assertIn("Istorija / Dnevnik", HTML_DASHBOARD)

    def test_04_wis_center_iso_and_usb_builder(self):
        # WIS ISO info i download
        self.assertIn("/api/wis/iso/info", HTML_DASHBOARD)
        self.assertIn("/api/wis/iso/download", HTML_DASHBOARD)
        self.assertIn('id="wis-iso-filename"', HTML_DASHBOARD)
        self.assertIn('id="wis-iso-version"', HTML_DASHBOARD)
        self.assertIn('id="wis-iso-size"', HTML_DASHBOARD)
        self.assertIn('id="wis-iso-sha256"', HTML_DASHBOARD)
        self.assertIn('id="wis-iso-status-badge"', HTML_DASHBOARD)

        # Interaktivni UEFI USB Builder
        self.assertIn("/api/wis/usb/list", HTML_DASHBOARD)
        self.assertIn('id="wis-usb-select"', HTML_DASHBOARD)
        self.assertIn('id="wis-confirm-wipe"', HTML_DASHBOARD)
        self.assertIn('id="btn-burn-usb"', HTML_DASHBOARD)
        self.assertIn("Potvrđujem brisanje svih podataka sa izabranog USB diska", HTML_DASHBOARD)
        self.assertIn("Kreiraj Butabilni USB", HTML_DASHBOARD)

        # USB Burn SSE i POST
        self.assertIn("/api/wis/usb/burn", HTML_DASHBOARD)
        self.assertIn("/api/wis/usb/progress", HTML_DASHBOARD)
        self.assertIn('id="wis-burn-progress-box"', HTML_DASHBOARD)
        self.assertIn('id="wis-burn-bar-fill"', HTML_DASHBOARD)

    def test_05_windows_11_updates_tab(self):
        # API i status
        self.assertIn("/api/updates/status", HTML_DASHBOARD)
        self.assertIn('id="updates-status-text"', HTML_DASHBOARD)
        self.assertIn('id="updates-directory-text"', HTML_DASHBOARD)
        self.assertIn('id="updates-total-count"', HTML_DASHBOARD)
        self.assertIn('id="updates-total-size"', HTML_DASHBOARD)
        self.assertIn('id="updates-zfs-badge"', HTML_DASHBOARD)
        self.assertIn("ZFS Keš Aktivan", HTML_DASHBOARD)

        # Tabela paketa i pretraga
        self.assertIn('id="updates-table-body"', HTML_DASHBOARD)
        self.assertIn('id="updates-search"', HTML_DASHBOARD)
        self.assertIn("filterUpdatesTable", HTML_DASHBOARD)

    def test_06_modals(self):
        # 1. Live Log Terminal modal
        self.assertIn('id="log-modal"', HTML_DASHBOARD)
        self.assertIn('id="terminal-content"', HTML_DASHBOARD)
        self.assertIn("openLogModal", HTML_DASHBOARD)

        # 2. Servisni Karton modal
        self.assertIn('id="report-modal"', HTML_DASHBOARD)
        self.assertIn('id="report-modal-content"', HTML_DASHBOARD)
        self.assertIn("openReportModal", HTML_DASHBOARD)

        # 3. Servisna Istorija i Dnevnik modal
        self.assertIn('id="history-modal"', HTML_DASHBOARD)
        self.assertIn('id="history-modal-content"', HTML_DASHBOARD)
        self.assertIn("openHistoryModal", HTML_DASHBOARD)
        self.assertIn("renderMarkdown", HTML_DASHBOARD)
        self.assertIn("SERVISNI_DNEVNIK.md", HTML_DASHBOARD)


    def test_07_cockpit_screen_mirror_and_telemetry(self):
        # 1. Cockpit Screen Mirror kontejneri i elementi (Active Live Feed i Standby Režim)
        self.assertIn('id="cockpit-detail-pane"', HTML_DASHBOARD)
        self.assertIn("screen-mirror-frame", HTML_DASHBOARD)
        self.assertIn('id="screen-mirror-viewport"', HTML_DASHBOARD)
        self.assertIn('id="screen-mirror-badge"', HTML_DASHBOARD)
        self.assertIn('id="mirror-live-container"', HTML_DASHBOARD)
        self.assertIn('id="cockpit-screen-img"', HTML_DASHBOARD)
        self.assertIn('id="mirror-standby-container"', HTML_DASHBOARD)
        self.assertIn("Povezano na uređaj. Čeka se klijentski video/skrinšot strim sa radne stanice", HTML_DASHBOARD)

        # 2. 1 Hz polling mehanizam i /screen-frame.jpg ruta u JavaScript-u
        self.assertIn("/screen-frame.jpg?t=", HTML_DASHBOARD)
        self.assertIn("function startCockpitScreenMirror", HTML_DASHBOARD)
        self.assertIn("function stopCockpitScreenMirror", HTML_DASHBOARD)
        self.assertIn("function pollCockpitScreen", HTML_DASHBOARD)
        self.assertIn("testImg.onload", HTML_DASHBOARD)
        self.assertIn("testImg.onerror", HTML_DASHBOARD)
        self.assertIn("🟢 1 Hz LIVE", HTML_DASHBOARD)
        self.assertIn("Čeka se klijentski strim", HTML_DASHBOARD)

        # 3. Hardverska telemetrija i merni instrumenti (CPU/RAM/Termika/SMART NVMe)
        self.assertIn("cockpit-gauge-cpu-pct", HTML_DASHBOARD)
        self.assertIn("cockpit-gauge-cpu-bar", HTML_DASHBOARD)
        self.assertIn("Termika CPU", HTML_DASHBOARD)
        self.assertIn("cockpit-gauge-temp-val", HTML_DASHBOARD)
        self.assertIn("-- °C", HTML_DASHBOARD)
        self.assertIn("cockpit-gauge-ram-pct", HTML_DASHBOARD)
        self.assertIn("cockpit-gauge-ram-val", HTML_DASHBOARD)
        self.assertIn("cockpit-gauge-smart-cat", HTML_DASHBOARD)
        self.assertIn("cockpit-gauge-smart-val", HTML_DASHBOARD)
        self.assertIn("NVMe SMART Zdravlje", HTML_DASHBOARD)
        self.assertIn("function updateCockpitMetricsInPlace", HTML_DASHBOARD)

    def test_08_screen_mirror_design_compliance(self):
        """Verifikacija usklađenosti Screen Mirror UI komponenti sa DESIGN.md standardima."""
        # CSS klase i struktura po DESIGN.md
        self.assertIn(".mirror-live-container", HTML_DASHBOARD)
        self.assertIn(".mirror-live-img-box", HTML_DASHBOARD)
        self.assertIn(".cockpit-screen-img", HTML_DASHBOARD)
        self.assertIn(".mirror-live-overlay-footer", HTML_DASHBOARD)
        self.assertIn(".mirror-live-stream-label", HTML_DASHBOARD)
        self.assertIn(".mirror-standby-container", HTML_DASHBOARD)
        self.assertIn(".screen-mirror-viewport", HTML_DASHBOARD)
        self.assertIn(".screen-mirror-viewport::after", HTML_DASHBOARD)

        # DESIGN.md tokeni i dark enterprise estetika
        self.assertIn("var(--radius-md)", HTML_DASHBOARD)
        self.assertIn("var(--radius-sm)", HTML_DASHBOARD)
        self.assertIn("var(--border)", HTML_DASHBOARD)
        self.assertIn("var(--font-mono)", HTML_DASHBOARD)
        self.assertIn("var(--success)", HTML_DASHBOARD)
        self.assertIn("var(--accent)", HTML_DASHBOARD)
        self.assertIn("backdrop-filter: blur(", HTML_DASHBOARD)

        # Ključni DOM identifikatori
        self.assertIn('id="mirror-live-container"', HTML_DASHBOARD)
        self.assertIn('id="mirror-standby-container"', HTML_DASHBOARD)
        self.assertIn('id="cockpit-screen-img"', HTML_DASHBOARD)
        self.assertIn('id="screen-mirror-badge"', HTML_DASHBOARD)
        self.assertIn('id="screen-mirror-viewport"', HTML_DASHBOARD)

        # JavaScript funkcije upravljanja
        self.assertIn("function startCockpitScreenMirror(devKey)", HTML_DASHBOARD)
        self.assertIn("function stopCockpitScreenMirror()", HTML_DASHBOARD)
        self.assertIn("function pollCockpitScreen()", HTML_DASHBOARD)
        self.assertIn("testImg.onload", HTML_DASHBOARD)
        self.assertIn("testImg.onerror", HTML_DASHBOARD)

    def test_09_remote_commands_and_quick_actions(self):
        """Verifikacija Quick Action kontrola za daljinsko upravljanje (Remote BIOS Reboot UI)."""
        # 1. Quick Action dugmad u Cockpit radnoj traci
        self.assertIn("Restart u UEFI/BIOS", HTML_DASHBOARD)
        self.assertIn("<span>⚡</span> Restart u UEFI/BIOS", HTML_DASHBOARD)
        self.assertIn("Mrežni But (PXE)", HTML_DASHBOARD)
        self.assertIn("<span>🔄</span> Mrežni But (PXE)", HTML_DASHBOARD)
        self.assertIn("btn-cockpit btn-warning", HTML_DASHBOARD)
        self.assertIn("btn-cockpit btn-secondary", HTML_DASHBOARD)

        # 2. Akcije komandi
        self.assertIn("'reboot_uefi'", HTML_DASHBOARD)
        self.assertIn("'reboot_pxe'", HTML_DASHBOARD)

        # 3. Potvrdni modal i Toast kontejner
        self.assertIn('id="command-confirm-modal"', HTML_DASHBOARD)
        self.assertIn('id="command-confirm-message"', HTML_DASHBOARD)
        self.assertIn('id="btn-confirm-command-action"', HTML_DASHBOARD)
        self.assertIn("Potvrda Daljinske Komande", HTML_DASHBOARD)
        self.assertIn("Da li ste sigurni da želite da pošaljete komandu", HTML_DASHBOARD)
        self.assertIn('id="toast-container"', HTML_DASHBOARD)

        # 4. JavaScript funkcije i API endpoint pozivi
        self.assertIn("function sendDeviceCommand(devKey, action, actionLabel)", HTML_DASHBOARD)
        self.assertIn("function closeCommandConfirmModal()", HTML_DASHBOARD)
        self.assertIn("function executeConfirmedCommand()", HTML_DASHBOARD)
        self.assertIn("function showToast(message, type", HTML_DASHBOARD)
        self.assertIn("/api/devices/' + encodeURIComponent(devKey) + '/command", HTML_DASHBOARD)
        self.assertIn("JSON.stringify({ action: action, params: {} })", HTML_DASHBOARD)
        self.assertIn("Biće izvršena pri sledećem heartbeat-u.", HTML_DASHBOARD)

        # 5. CSS stilovi (DESIGN.md tamna tema i tokeni)
        self.assertIn(".btn-warning", HTML_DASHBOARD)
        self.assertIn(".btn-secondary", HTML_DASHBOARD)
        self.assertIn(".toast-container", HTML_DASHBOARD)
        self.assertIn(".toast", HTML_DASHBOARD)
        self.assertIn(".toast-success", HTML_DASHBOARD)
        self.assertIn(".toast-error", HTML_DASHBOARD)
        self.assertIn("var(--warning)", HTML_DASHBOARD)
        self.assertIn("var(--warning-glow)", HTML_DASHBOARD)


if __name__ == "__main__":
    unittest.main()

