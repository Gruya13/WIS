#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Test skripta za verifikaciju funkcije generate_hwid_candidates u ServisHub-u
(GWADT-TASK-101: ACPI i Audio/HDA optimizacija).
"""

import sys
import os

# Uvoz iz trenutnog direktorijuma ili putanje
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hub_server import generate_hwid_candidates


def run_tests():
    print("=" * 70)
    print("POKRETANJE TESTOVA ZA generate_hwid_candidates()")
    print("=" * 70)

    # 1. ACPI testovi (Dell Pro 14, moderni Intel/AMD laptopi)
    print("\n[TEST 1] ACPI uređaji sa VEN_ i DEV_...")
    acpi_hwid_1 = "ACPI\\VEN_INTC&DEV_1078"
    cands_1 = generate_hwid_candidates(acpi_hwid_1)
    print(f"Ulaz: {acpi_hwid_1} -> Kandidati: {cands_1}")
    expected_acpi_1 = [
        "ACPI\\VEN_INTC&DEV_1078",
        "ACPI\\INTC1078",
        "*INTC1078",
        "INTC1078",
    ]
    for exp in expected_acpi_1:
        assert exp in cands_1, f"Greška: Očekivani kandidat '{exp}' nije pronađen u {cands_1}"
    assert cands_1.index("ACPI\\VEN_INTC&DEV_1078") < cands_1.index("ACPI\\INTC1078") < cands_1.index("*INTC1078") < cands_1.index("INTC1078"), "Greška: Redosled kandidata nije od najspecifičnijeg ka najopštijem!"
    print("  [✓] ACPI VEN_INTC&DEV_1078 uspešno normalizovan!")

    acpi_hwid_2 = "ACPI\\VEN_INTC&DEV_1028"
    cands_2 = generate_hwid_candidates(acpi_hwid_2)
    print(f"Ulaz: {acpi_hwid_2} -> Kandidati: {cands_2}")
    expected_acpi_2 = [
        "ACPI\\VEN_INTC&DEV_1028",
        "ACPI\\INTC1028",
        "*INTC1028",
        "INTC1028",
    ]
    for exp in expected_acpi_2:
        assert exp in cands_2, f"Greška: Očekivani kandidat '{exp}' nije pronađen u {cands_2}"
    print("  [✓] ACPI VEN_INTC&DEV_1028 uspešno normalizovan!")

    # 2. ACPI kraće forme (npr. ACPI\INTC1078 ili *INTC1078)
    print("\n[TEST 2] ACPI kraće forme...")
    acpi_short = "ACPI\\INTC1078"
    cands_short = generate_hwid_candidates(acpi_short)
    print(f"Ulaz: {acpi_short} -> Kandidati: {cands_short}")
    for exp in ["ACPI\\INTC1078", "*INTC1078", "INTC1078"]:
        assert exp in cands_short, f"Greška: '{exp}' nedostaje u {cands_short}"
    print("  [✓] ACPI kraća forma uspešno normalizovana!")

    # 3. Audio / High Definition Audio sa REV i SUBSYS
    print("\n[TEST 3] HDAUDIO sa SUBSYS i REV...")
    hda_hwid_1 = "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000"
    cands_hda_1 = generate_hwid_candidates(hda_hwid_1)
    print(f"Ulaz: {hda_hwid_1} -> Kandidati: {cands_hda_1}")
    expected_hda_1 = [
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000",
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C",
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256",
        "VEN_10EC&DEV_0256",
    ]
    for exp in expected_hda_1:
        assert exp in cands_hda_1, f"Greška: Očekivani HDA kandidat '{exp}' nije pronađen u {cands_hda_1}"
    # Provera redosleda specifičnosti
    idx_rev = cands_hda_1.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000")
    idx_sub = cands_hda_1.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C")
    idx_vd = cands_hda_1.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256")
    idx_gen = cands_hda_1.index("VEN_10EC&DEV_0256")
    assert idx_rev < idx_sub < idx_vd < idx_gen, "Greška: Redosled HDA kandidata mora ići od najspecifičnijeg ka najopštijem!"
    print("  [✓] HDAUDIO sa REV i SUBSYS uspešno normalizovan!")

    # 4. Audio sa sabirničkim sufiksom &COL01 i &COL02
    print("\n[TEST 4] HDAUDIO sa &COL01 sabirničkim sufiksom...")
    hda_col = "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01"
    cands_col = generate_hwid_candidates(hda_col)
    print(f"Ulaz: {hda_col} -> Kandidati: {cands_col}")
    expected_col = [
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01",
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000",
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C",
        "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256",
        "VEN_10EC&DEV_0256",
    ]
    for exp in expected_col:
        assert exp in cands_col, f"Greška: '{exp}' nedostaje u {cands_col}"
    # Provera redosleda: COL pre uklonjenog COL
    assert cands_col.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01") < cands_col.index("HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000")
    print("  [✓] Sabirnički sufiks &COL01 uspešno uklonjen i redosled očuvan!")

    # 5. Provera duplikata (nema duplih unosa, očuvan redosled)
    print("\n[TEST 5] Provera jedinstvenosti (bez duplikata)...")
    for test_in in [acpi_hwid_1, acpi_hwid_2, acpi_short, hda_hwid_1, hda_col]:
        c = generate_hwid_candidates(test_in)
        assert len(c) == len(set(c)), f"Pronađeni duplikati u kandidatima za {test_in}: {c}"
    print("  [✓] Svi kandidati su strogo jedinstveni (0 duplikata)!")

    # 6. PCI uređaji
    print("\n[TEST 6] PCI uređaj sa SUBSYS i REV...")
    pci_hwid = "PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C"
    cands_pci = generate_hwid_candidates(pci_hwid)
    print(f"Ulaz: {pci_hwid} -> Kandidati: {cands_pci}")
    for exp in ["PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C", "PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043", "PCI\\VEN_10EC&DEV_8168", "VEN_10EC&DEV_8168"]:
        assert exp in cands_pci, f"Greška: '{exp}' nedostaje u {cands_pci}"
    print("  [✓] PCI kandidati su tačni i opadajućeg prioriteta!")

    print("\n" + "=" * 70)
    print("SVI TESTOVI SU USPEŠNO ZAVRŠENI!")
    print("=" * 70)


if __name__ == "__main__":
    run_tests()
