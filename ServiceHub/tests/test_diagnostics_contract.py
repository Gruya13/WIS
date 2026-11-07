"""Scenario testovi stvarnog kartona, FleetStore i browser status funkcija."""
from pathlib import Path
import shutil
import subprocess
import unittest
from servishub.reports import diagnostics_contract, generate_markdown_report
from servishub.telemetry import FleetStore
from servishub.web.dashboard import HTML_DASHBOARD


class DiagnosticsTests(unittest.TestCase):
    def test_unknown_sensor_does_not_claim_verified_cooling(self):
        data = {'status': 'COMPLETED', 'diagnostics_status': 'SENSOR_UNAVAILABLE',
                'cooling_verified': False, 'manual_check_required': True}
        md = generate_markdown_report(data)
        self.assertIn('Status Instalacije:** **COMPLETED', md)
        self.assertIn('Ručna provera potrebna:** Da', md)
        self.assertIn('Automatski provereno:** Ne', md)
        self.assertTrue(md.startswith('# ⚠️'))

    def test_critical_evidence_overrides_success_flags(self):
        data = {'diagnostics_status': 'OK', 'cooling_verified': True,
                'max_cpu_temp_c': 98, 'manual_check_required': False}
        result = diagnostics_contract(data)
        self.assertEqual(result['diagnostics_status'], 'THERMAL_CRITICAL')
        self.assertFalse(result['cooling_verified'])
        self.assertTrue(result['manual_check_required'])

    def test_absent_diagnostic_result_is_not_pass(self):
        result = diagnostics_contract({'status': 'COMPLETED'})
        self.assertEqual(result['diagnostics_status'], 'NOT_VERIFIED')
        self.assertTrue(result['manual_check_required'])

    def test_actual_pass_is_distinct_from_error_and_cancel(self):
        passed = diagnostics_contract({'diagnostics_status': 'OK', 'cooling_verified': True})
        self.assertTrue(passed['cooling_verified'])
        for status in ('WARN', 'ERROR', 'CANCELLED'):
            self.assertFalse(diagnostics_contract({'diagnostics_status': status, 'cooling_verified': True})['cooling_verified'])

    def test_phase_five_heartbeat_and_session_do_not_complete_installation(self):
        store = FleetStore()
        device = store.record_heartbeat({'computer_name': 'TEST', 'phase': 5}, '127.0.0.1')
        self.assertEqual(store.get_devices_summary()[device]['status_label'], 'Aktivno')
        session = store.record_session_state('test-session', {'computer_name': 'TEST', 'phase': 5}, '127.0.0.1')
        self.assertEqual(session['status'], 'ACTIVE')

    def test_explicit_completion_and_thermal_priority_in_fleet(self):
        store = FleetStore()
        device = store.record_lifecycle_complete({'computer_name': 'TEST', 'diagnostics_status': 'SENSOR_UNAVAILABLE'}, '127.0.0.1')
        summary = store.get_devices_summary()[device]
        self.assertEqual(summary['phase'], 6)
        self.assertEqual(summary['status_label'], 'Zavrseno')
        self.assertTrue(summary['manual_check_required'])
        store.record_lifecycle_complete({'computer_name': 'TEST', 'diagnostics_status': 'THERMAL_CRITICAL'}, '127.0.0.1')
        self.assertEqual(store.get_devices_summary()[device]['status_label'], 'NEEDS_ATTENTION')

    def test_real_browser_status_functions(self):
        if not shutil.which('node'):
            self.skipTest('Node nije dostupan za browser status funkcije.')
        start = HTML_DASHBOARD.index('// WIS_STATUS_CONTRACT_START')
        end = HTML_DASHBOARD.index('// WIS_STATUS_CONTRACT_END')
        functions = HTML_DASHBOARD[start:end]
        scenarios = """
const assert = require('node:assert/strict');
assert.equal(deviceIsCompleted({phase:5}), false);
assert.equal(deviceIsCompleted({phase:6}), false);
assert.equal(deviceIsCompleted({phase:6,status_label:'Zavrseno'}), true);
assert.equal(deviceIsCompleted({status_label:'Zavrseno', diagnostics_status:'THERMAL_CRITICAL'}), false);
assert.match(deviceLiveStatus({status_label:'Zavrseno',cooling_verified:false}), /ručna provera/);
assert.doesNotMatch(deviceLiveStatus({phase:5}), /provereno prema/);
"""
        subprocess.run(['node', '-e', functions + scenarios], check=True, capture_output=True, text=True)
