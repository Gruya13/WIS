"""
servishub.drivers.hwid
======================
Normalizacija hardverskih identifikatora (HWID) i generisanje kaskadnih kandidata
za pretragu Microsoft Update Catalog-a.
"""

import re


def generate_hwid_candidates(hwid: str) -> list:
    """
    Generiše niz hardverskih identifikatora od najspecifičnijeg ka najopštijem (fallback).
    Podržava ACPI, HDAUDIO/Audio, PCI i USB uređaje sa inteligentnom normalizacijom
    (Dell Pro 14 ACPI normalizacija, uklanjanje sabirničkih &COLxx i &REV_xxxx sufiksa).

    Primeri:
      ACPI\\VEN_INTC&DEV_1078
      -> 1. ACPI\\VEN_INTC&DEV_1078
      -> 2. ACPI\\INTC1078
      -> 3. *INTC1078
      -> 4. INTC1078
      -> 5. VEN_INTC&DEV_1078

      HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01
      -> 1. HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000&COL01
      -> 2. HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C&REV_1000
      -> 3. HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256&SUBSYS_10280B8C
      -> 4. HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256
      -> 5. VEN_10EC&DEV_0256

      PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C
      -> 1. PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043&REV_2C
      -> 2. PCI\\VEN_10EC&DEV_8168&SUBSYS_85701043
      -> 3. PCI\\VEN_10EC&DEV_8168
      -> 4. VEN_10EC&DEV_8168
    """
    if not hwid or not isinstance(hwid, str):
        return []

    candidates = []
    seen = set()

    def add_cand(val: str):
        if not val:
            return
        clean_val = re.sub(r'\\+', r'\\', str(val).strip())
        if not clean_val:
            return
        upper_val = clean_val.upper()
        if upper_val not in seen:
            seen.add(upper_val)
            candidates.append(upper_val)

    clean = re.sub(r'\\+', r'\\', hwid.strip())
    if not clean:
        return []

    clean_upper = clean.upper()
    add_cand(clean_upper)

    # 1. ACPI uređaji (Dell Pro 14, moderni laptopi, čipset kontroleri)
    is_acpi = clean_upper.startswith("ACPI\\") or clean_upper.startswith("*") or "ACPI" in clean_upper
    acpi_ven_dev = re.search(r'VEN_([A-Za-z0-9_]+)&DEV_([A-Za-z0-9_]+)', clean_upper)

    if (clean_upper.startswith("ACPI\\VEN_") or (is_acpi and acpi_ven_dev)) and acpi_ven_dev:
        ven = acpi_ven_dev.group(1).upper()
        dev = acpi_ven_dev.group(2).upper()
        add_cand(f"ACPI\\VEN_{ven}&DEV_{dev}")
        add_cand(f"ACPI\\{ven}{dev}")
        add_cand(f"*{ven}{dev}")
        add_cand(f"{ven}{dev}")
        add_cand(f"VEN_{ven}&DEV_{dev}")
    elif is_acpi:
        acpi_short_m = re.search(r'(?:ACPI\\|\*)([A-Za-z0-9_]+)', clean_upper)
        if acpi_short_m:
            ident = acpi_short_m.group(1).upper()
            add_cand(f"ACPI\\{ident}")
            add_cand(f"*{ident}")
            add_cand(ident)
            m_parts = re.match(r'^([A-Za-z]{3,4})([0-9A-Za-z]{3,5})$', ident)
            if m_parts:
                v = m_parts.group(1)
                d = m_parts.group(2)
                add_cand(f"ACPI\\VEN_{v}&DEV_{d}")
                add_cand(f"VEN_{v}&DEV_{d}")

    # 2. High Definition Audio (HDAUDIO, INTELAUDIO ili sabirnički FUNC_xx audio uređaji)
    is_audio = "HDAUDIO" in clean_upper or "INTELAUDIO" in clean_upper or "FUNC_" in clean_upper
    if is_audio:
        curr_audio = clean_upper
        if re.search(r'&COL[0-9A-Fa-f]+', curr_audio):
            curr_audio = re.sub(r'&COL[0-9A-Fa-f]+.*$', '', curr_audio)
            add_cand(curr_audio)

        if re.search(r'&REV_[0-9A-Fa-f]+', curr_audio):
            curr_no_rev = re.sub(r'&REV_[0-9A-Fa-f]+.*$', '', curr_audio)
            add_cand(curr_no_rev)
            curr_audio_for_sub = curr_no_rev
        else:
            curr_audio_for_sub = curr_audio

        if re.search(r'&SUBSYS_[0-9A-Fa-f]+', curr_audio_for_sub):
            curr_no_sub = re.sub(r'&SUBSYS_[0-9A-Fa-f]+.*$', '', curr_audio_for_sub)
            add_cand(curr_no_sub)

        hda_vd = re.search(r'((?:HDAUDIO|INTELAUDIO|[A-Za-z0-9_]+)\\FUNC_\d+&VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4})', clean_upper)
        if hda_vd:
            add_cand(hda_vd.group(1))

        v_dev = re.search(r'(VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4})', clean_upper)
        if v_dev:
            add_cand(v_dev.group(1))

    # 3. PCI uređaji
    pci_full = re.search(r'(PCI\\VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4}&SUBSYS_[0-9A-Fa-f]{8}&REV_[0-9A-Fa-f]{2,4})', clean_upper)
    if pci_full:
        add_cand(pci_full.group(1))

    pci_sub = re.search(r'(PCI\\VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4}&SUBSYS_[0-9A-Fa-f]{8})', clean_upper)
    if pci_sub:
        add_cand(pci_sub.group(1))

    pci_vd = re.search(r'(PCI\\VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4})', clean_upper)
    if pci_vd:
        vd = pci_vd.group(1)
        add_cand(vd)
        short_vd = vd.replace("PCI\\", "")
        add_cand(short_vd)

    # 4. USB uređaji
    usb_rev = re.search(r'(USB\\VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}&REV_[0-9A-Fa-f]{2,4})', clean_upper)
    if usb_rev:
        add_cand(usb_rev.group(1))

    usb_vp = re.search(r'(USB\\VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4})', clean_upper)
    if usb_vp:
        vp = usb_vp.group(1)
        add_cand(vp)
        short_vp = vp.replace("USB\\", "")
        add_cand(short_vp)

    return candidates
