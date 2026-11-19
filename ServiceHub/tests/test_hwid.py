"""
tests.test_hwid
===============
Verifikacija funkcije generate_hwid_candidates u servishub.drivers.hwid modulu.
"""

import unittest
from servishub.drivers.hwid import generate_hwid_candidates


class TestHwidCandidates(unittest.TestCase):

    def test_acpi_ven_dev(self):
        hwid = "ACPI\\VEN_INTC&DEV_1078"
        cands = generate_hwid_candidates(hwid)
        expected = [
            "ACPI\\VEN_INTC&DEV_1078",
            "ACPI\\INTC1078",
            "*INTC1078",
            "INTC1078",
            "VEN_INTC&DEV_1078",
        ]
        for exp in expected:
            self.assertIn(exp, cands)
        # Provera redosleda od najspecifičnijeg ka najopštijem
        self.assertLess(cands.index("ACPI\\VEN_INTC&DEV_1078"), cands.index("ACPI\\INTC1078"))
        self.assertLess(cands.index("ACPI\\INTC1078"), cands.index("*INTC1078"))
        self.assertLess(cands.index("*INTC1078"), cands.index("INTC1078"))

    def test_acpi_short_form(self):
        hwid = "ACPI\\INTC1078"
        cands = generate_hwid_candidates(hwid)
        for exp in ["ACPI\\INTC1078", "*INTC1078", "INTC1078"]:
            self.assertIn(exp, cands)

    def test_hdaudio_subsys_rev_col(self):
        hwid = "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01"
        cands = generate_hwid_candidates(hwid)
        expected = [
            "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01",
            "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000",
            "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C",
            "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256",
            "VEN_10EC&DEV_0256",
        ]
        for exp in expected:
            self.assertIn(exp, cands)
        self.assertLess(cands.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000"), cands.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256"))

    def test_pci_device(self):
        hwid = "PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C"
        cands = generate_hwid_candidates(hwid)
        self.assertIn("PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C", cands)
        self.assertIn("PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043", cands)
        self.assertIn("PCI\\VEN_10EC&DEV_8168", cands)
        self.assertIn("VEN_10EC&DEV_8168", cands)

    def test_usb_device(self):
        hwid = "USB\\VID_046D&PID_C52B&REV_2401"
        cands = generate_hwid_candidates(hwid)
        self.assertIn("USB\\VID_046D&PID_C52B&REV_2401", cands)
        self.assertIn("USB\\VID_046D&PID_C52B", cands)
        self.assertIn("VID_046D&PID_C52B", cands)

    def test_empty_input(self):
        self.assertEqual(generate_hwid_candidates(""), [])
        self.assertEqual(generate_hwid_candidates(None), [])


if __name__ == "__main__":
    unittest.main()
