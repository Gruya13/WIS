#!/usr/bin/env python3
"""
Test-LocalizationParity.py
==========================
CI paritetni gate za V343-08 (zh-CN / 简体中文 lokalizacija u WIS.UI i WIS.HwTest).
"""

import pathlib
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
UI_DIR = ROOT / "UI"
HWTEST_DIR = UI_DIR / "HwTest"
LOC_CS_PATH = UI_DIR / "LocalizationService.cs"
XAML_PATH = UI_DIR / "MainWindow.xaml"

SR_DIACRITIC_RX = re.compile(r"[čćšžđČĆŠŽĐ]")
FMT_PLACEHOLDER_RX = re.compile(r"\{(\d+)(?::[^}]*)?\}")


def extract_dict_entries(src, dict_name):
    """Izvlači mapu ključ -> vrednost za zadati rečnik iz LocalizationService.cs."""
    entries = {}
    duplicates = []
    pattern = re.compile(
        rf'{re.escape(dict_name)}\s*\[\s*"([^"]+)"\s*\]\s*=\s*"((?:\\.|[^"\\])*)"\s*;'
    )
    for m in pattern.finditer(src):
        key, val = m.group(1), m.group(2)
        if key in entries:
            duplicates.append(key)
        entries[key] = val
    return entries, duplicates


def _parse_cs_string_literal_expr(src, pos):
    """Parsira jedan ili više '+' nadovezanih C# string literala (uklj. @\"...\")."""
    n = len(src)
    parts = []
    saw_literal = False

    while True:
        while pos < n and src[pos] in " \t\r\n":
            pos += 1
        if pos >= n:
            return ("".join(parts) if saw_literal else None), pos

        if src[pos : pos + 2] == '@"':
            saw_literal = True
            pos += 2
            buf = []
            while pos < n:
                if src[pos] == '"' and pos + 1 < n and src[pos + 1] == '"':
                    buf.append('"')
                    pos += 2
                elif src[pos] == '"':
                    pos += 1
                    break
                else:
                    buf.append(src[pos])
                    pos += 1
            parts.append("".join(buf))
        elif src[pos] == '"':
            saw_literal = True
            pos += 1
            buf = []
            while pos < n:
                if src[pos] == "\\" and pos + 1 < n:
                    buf.append(src[pos : pos + 2])
                    pos += 2
                elif src[pos] == '"':
                    pos += 1
                    break
                else:
                    buf.append(src[pos])
                    pos += 1
            parts.append("".join(buf))
        else:
            return ("".join(parts) if saw_literal else None), pos

        # Podrška za višerednu konkatenaciju literala pomoću '+'
        save_pos = pos
        while save_pos < n and src[save_pos] in " \t\r\n":
            save_pos += 1
        if save_pos < n and src[save_pos] == "+":
            next_pos = save_pos + 1
            while next_pos < n and src[next_pos] in " \t\r\n":
                next_pos += 1
            if next_pos < n and (src[next_pos] == '"' or src[next_pos : next_pos + 2] == '@"'):
                pos = next_pos
                continue
        return "".join(parts), pos


def scan_hwtest_pick_calls(src):
    """Pronalazi sve pozive HwTestLocalization.Pick(...) i PickFormat(...) u C# fajlu."""
    calls = []
    for m in re.finditer(r"HwTestLocalization\.(PickFormat|Pick)\s*\(", src):
        kind = m.group(1)
        pos = m.end()
        sr_val, pos = _parse_cs_string_literal_expr(src, pos)
        while pos < len(src) and src[pos] in " \t\r\n":
            pos += 1
        zh_val = None
        if pos < len(src) and src[pos] == ",":
            pos += 1
            zh_val, pos = _parse_cs_string_literal_expr(src, pos)
        calls.append((kind, sr_val, zh_val))
    return calls


class TestLocalizationParity(unittest.TestCase):
    def test_01_ui_dictionary_100_percent_parity_and_placeholders(self):
        """Proverava 100% paritet _srDict vs _zhDict, prazne stringove, zabranu SR dijakritika u ZH i {n} placeholdere."""
        loc_src = LOC_CS_PATH.read_text(encoding="utf-8")
        sr_dict, sr_dups = extract_dict_entries(loc_src, "_srDict")
        zh_dict, zh_dups = extract_dict_entries(loc_src, "_zhDict")

        self.assertGreater(len(sr_dict), 900, "Očekuje se kompletna lokalizacija (> 900 ključeva)")
        self.assertEqual(sr_dups, [], f"Duplikati ključeva u _srDict: {sr_dups}")
        self.assertEqual(zh_dups, [], f"Duplikati ključeva u _zhDict: {zh_dups}")

        missing_in_zh = sorted(set(sr_dict) - set(zh_dict))
        extra_in_zh = sorted(set(zh_dict) - set(sr_dict))
        self.assertEqual(missing_in_zh, [], f"Ključevi koji nedostaju u _zhDict: {missing_in_zh}")
        self.assertEqual(extra_in_zh, [], f"Višak ključeva u _zhDict: {extra_in_zh}")

        empty_sr = [k for k, v in sr_dict.items() if not v.strip()]
        empty_zh = [k for k, v in zh_dict.items() if not v.strip()]
        self.assertEqual(empty_sr, [], f"Prazne vrednosti u _srDict: {empty_sr}")
        self.assertEqual(empty_zh, [], f"Prazne vrednosti u _zhDict: {empty_zh}")

        bad_diacritics = [k for k, v in zh_dict.items() if SR_DIACRITIC_RX.search(v)]
        self.assertEqual(bad_diacritics, [], f"Srpski dijakritici pronađeni u _zhDict: {bad_diacritics}")

        mismatched_fmt = []
        for k in sorted(sr_dict):
            sr_ph = sorted(set(FMT_PLACEHOLDER_RX.findall(sr_dict[k])))
            zh_ph = sorted(set(FMT_PLACEHOLDER_RX.findall(zh_dict[k])))
            if sr_ph != zh_ph:
                mismatched_fmt.append((k, sr_ph, zh_ph))
        self.assertEqual(mismatched_fmt, [], f"Neusklađeni {{n}} placeholderi između SR i ZH: {mismatched_fmt}")

    def test_02_xaml_valid_xml_keys_and_no_hardcoded_v343_serbian_labels(self):
        """Proverava validnost MainWindow.xaml, sve {Binding Loc[Key]} reference i uklanjanje hardcoded srpskih UI labela."""
        loc_src = LOC_CS_PATH.read_text(encoding="utf-8")
        sr_dict, _ = extract_dict_entries(loc_src, "_srDict")
        zh_dict, _ = extract_dict_entries(loc_src, "_zhDict")

        xaml_raw = XAML_PATH.read_text(encoding="utf-8")
        ET.fromstring(xaml_raw)

        xaml_keys = set(re.findall(r"Loc\[([A-Za-z0-9_]+)\]", xaml_raw))
        self.assertGreater(len(xaml_keys), 100)
        self.assertEqual(sorted(xaml_keys - set(sr_dict)), [], "Nedostajući XAML Loc[] ključevi u _srDict")
        self.assertEqual(sorted(xaml_keys - set(zh_dict)), [], "Nedostajući XAML Loc[] ključevi u _zhDict")

        # Ukloniti XML komentare pre provere zaostalih hardcoded UI stringova
        xaml_no_comments = re.sub(r"<!--.*?-->", "", xaml_raw, flags=re.DOTALL)
        forbidden_hardcoded = (
            "INFORMACIJE O UREĐAJU",
            "DODATNE PERIFERIJE",
            "HARDVER MONITOR",
            "UPOZORENJA SA KOJIMA JE PROCES ZAVRŠEN",
            "KARTON UREĐAJA (SERVISHUB)",
            "AKTIVNI KORACI",
            "Pokreni interaktivne testove",
            "Proveri ponovo i nastavi",
        )
        for text in forbidden_hardcoded:
            self.assertNotIn(text, xaml_no_comments, f"Hardkodovani UI tekst '{text}' je ostao u MainWindow.xaml")

    def test_03_csharp_ui_loc_keys_exist_in_both_dictionaries(self):
        """Proverava da svi LocalizationService.Get(...) ključevi iz UI/*.cs postoje u _srDict i _zhDict."""
        loc_src = LOC_CS_PATH.read_text(encoding="utf-8")
        sr_dict, _ = extract_dict_entries(loc_src, "_srDict")
        zh_dict, _ = extract_dict_entries(loc_src, "_zhDict")

        used_keys = set()
        for cs_path in sorted(UI_DIR.glob("*.cs")):
            cs_src = cs_path.read_text(encoding="utf-8")
            for m in re.finditer(r'LocalizationService\.Get\(\s*"([^"]+)"', cs_src):
                used_keys.add(m.group(1))
            for m in re.finditer(r'LocalizationService\.Instance\.GetString\(\s*"([^"]+)"', cs_src):
                used_keys.add(m.group(1))

        self.assertGreater(len(used_keys), 400)
        self.assertEqual(sorted(used_keys - set(sr_dict)), [], "Nedostajući C# Get() ključevi u _srDict")
        self.assertEqual(sorted(used_keys - set(zh_dict)), [], "Nedostajući C# Get() ključevi u _zhDict")

    def test_04_hwtest_pick_and_pickformat_parity_no_serbian_diacritics(self):
        """Proverava sve HwTestLocalization.Pick/PickFormat pozive u UI/HwTest/**/*.cs."""
        total_calls = 0
        non_literal_calls = []
        empty_calls = []
        bad_diacritics = []
        bad_placeholders = []

        for cs_path in sorted(HWTEST_DIR.rglob("*.cs")):
            rel = str(cs_path.relative_to(ROOT))
            src = cs_path.read_text(encoding="utf-8")
            for kind, sr_val, zh_val in scan_hwtest_pick_calls(src):
                total_calls += 1
                if sr_val is None or zh_val is None:
                    non_literal_calls.append((rel, kind, sr_val, zh_val))
                    continue
                if not sr_val.strip() or not zh_val.strip():
                    empty_calls.append((rel, kind, sr_val, zh_val))
                if SR_DIACRITIC_RX.search(zh_val):
                    bad_diacritics.append((rel, kind, zh_val))
                if kind == "PickFormat":
                    sr_ph = sorted(set(FMT_PLACEHOLDER_RX.findall(sr_val)))
                    zh_ph = sorted(set(FMT_PLACEHOLDER_RX.findall(zh_val)))
                    if sr_ph != zh_ph:
                        bad_placeholders.append((rel, sr_ph, zh_ph, sr_val[:60]))

        self.assertGreater(total_calls, 800, "Očekuje se puna zh-CN lokalizacija WIS.HwTest (> 800 Pick/PickFormat poziva)")
        self.assertEqual(non_literal_calls, [], f"Neliteralni argumenti u Pick/PickFormat: {non_literal_calls}")
        self.assertEqual(empty_calls, [], f"Prazni argumenti u Pick/PickFormat: {empty_calls}")
        self.assertEqual(bad_diacritics, [], f"Srpski dijakritici pronađeni u ZH argumentu: {bad_diacritics}")
        self.assertEqual(bad_placeholders, [], f"Neusklađeni {{n}} placeholderi u PickFormat: {bad_placeholders}")

    def test_05_build_registration_and_live_language_sync_contract(self):
        """Proverava registraciju HwTestLocalization.cs na sva 4 build mesta i live sinhronizaciju jezika."""
        self.assertTrue((HWTEST_DIR / "Core" / "HwTestLocalization.cs").is_file())

        build_files = (
            (UI_DIR / "Build-UI.py", "HwTestLocalization.cs"),
            (HWTEST_DIR / "Build-HwTest.py", "HwTestLocalization.cs"),
            (UI_DIR / "WIS.UI.csproj", r"HwTest\Core\HwTestLocalization.cs"),
            (HWTEST_DIR / "WIS.HwTest.csproj", r"Core\HwTestLocalization.cs"),
        )
        for path, token in build_files:
            self.assertIn(token, path.read_text(encoding="utf-8"), f"{path.name} mora uključivati {token}")

        loc_src = LOC_CS_PATH.read_text(encoding="utf-8")
        self.assertIn("WIS.HwTest.Core.HwTestLocalization.SetLanguage(", loc_src)

        prog_src = (HWTEST_DIR / "Program.cs").read_text(encoding="utf-8")
        self.assertIn("HwTestLocalization.SetLanguage(", prog_src)
        self.assertIn("--lang", prog_src)

        shell_src = (HWTEST_DIR / "HwTestShell.cs").read_text(encoding="utf-8")
        self.assertIn("HwTestLocalization.LanguageChanged +=", shell_src)
        self.assertIn("HwTestLocalization.LanguageChanged -=", shell_src)
        self.assertIn("public void RefreshLocalization()", shell_src)


if __name__ == "__main__":
    unittest.main()
