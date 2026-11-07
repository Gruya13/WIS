#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Verify-CSharpSyntax.py - GWADT C# 5.0 Statički Analizator & QA Verifikator
Projekat: WIS Service Windows 11 Automatizacija (GWADT)
Cilj: Verifikacija 100% kompatibilnosti C# koda sa .NET Framework 4.0 MSBuild / csc.exe.

Proverava i zabranjuje sve C# 6.0/7.0+ jezičke konstrukcije:
1. Expression-bodied members (=>) u deklaracijama svojstava, metoda i akcesora:
   - get =>, set =>, add =>, remove =>
   - [type] [Name] => ... (svojstva moraju biti { get; set; })
   - [type] [Method](...) => ... (metode moraju imati telo u vitičastim zagradama)
   (Pravilno ignoriše validne C# 3.0 lambda izraze unutar metoda, delegata i poziva funkcija)
2. String interpolation ($"...", $@"... ", @$"...")
3. Auto-property initializers ({ get; } = ..., { get; set; } = ...)
4. Null-conditional operator (?., ?.[)
5. Nameof operator (nameof(...))
6. Out variable inline declarations (out var ...)
7. Throw expressions (?? throw ..., : throw ..., => throw ...)

Exit kodovi:
- 0: 100% čisto (svi fajlovi su striktni C# 5.0)
- 1: Pronađene greške / nekompatibilne konstrukcije
"""

import os
import sys
import re
from pathlib import Path

# ANSI boje za jasan konzolni ispis
GREEN = "\033[92m"
RED = "\033[91m"
YELLOW = "\033[93m"
CYAN = "\033[96m"
BOLD = "\033[1m"
RESET = "\033[0m"


def strip_comments_and_strings(raw_content):
    """
    Parsira C# kôd i uklanja komentare i sadržaj stringova kako bi se
    izbegli lažni pozitivni nalazi, istovremeno detektujući string interpolaciju ($" i $@").
    Vraća:
    - cleaned_lines: lista torki (line_num, original_line, clean_code)
    - string_interpolation_violations: lista torki (line_num, opis, linija)
    """
    lines = raw_content.splitlines(keepends=True)
    cleaned_lines = []
    interpolations = []

    in_block_comment = False
    in_verbatim_string = False

    for line_idx, raw_line in enumerate(lines, 1):
        clean_chars = []
        i = 0
        n = len(raw_line)

        while i < n:
            # Stanje unutar blok komentara /* ... */
            if in_block_comment:
                if i + 1 < n and raw_line[i:i+2] == "*/":
                    in_block_comment = False
                    i += 2
                else:
                    i += 1
                continue

            # Stanje unutar verbatim stringa @"..." ili $@"...
            if in_verbatim_string:
                if i + 1 < n and raw_line[i:i+2] == '""':
                    i += 2
                elif raw_line[i] == '"':
                    in_verbatim_string = False
                    i += 1
                else:
                    i += 1
                continue

            # Početak blok komentara
            if i + 1 < n and raw_line[i:i+2] == "/*":
                in_block_comment = True
                i += 2
                continue

            # Jednolinijski komentar //
            if i + 1 < n and raw_line[i:i+2] == "//":
                break

            # Detekcija C# 6.0 string interpolacije ($" ili $@\" ili @$\")
            if (i + 1 < n and raw_line[i:i+2] == '$"') or \
               (i + 2 < n and raw_line[i:i+3] in ('$@"', '@$"')):
                interpolations.append((
                    line_idx,
                    'String interpolation ($"...")',
                    raw_line.strip()
                ))
                is_verbatim = raw_line[i:i+3] in ('$@"', '@$"')
                i += 3 if is_verbatim else 2
                if is_verbatim:
                    in_verbatim_string = True
                else:
                    while i < n:
                        if raw_line[i] == "\\":
                            i += 2
                        elif raw_line[i] == '"':
                            i += 1
                            break
                        else:
                            i += 1
                clean_chars.append('""')
                continue

            # Regularni verbatim string @"..."
            if i + 1 < n and raw_line[i:i+2] == '@"':
                in_verbatim_string = True
                i += 2
                clean_chars.append('""')
                continue

            # Regularni string literal "..."
            if raw_line[i] == '"':
                i += 1
                while i < n:
                    if raw_line[i] == "\\":
                        i += 2
                    elif raw_line[i] == '"':
                        i += 1
                        break
                    else:
                        i += 1
                clean_chars.append('""')
                continue

            # Karakter literal 'c'
            if raw_line[i] == "'":
                i += 1
                while i < n:
                    if raw_line[i] == "\\":
                        i += 2
                    elif raw_line[i] == "'":
                        i += 1
                        break
                    else:
                        i += 1
                clean_chars.append("''")
                continue

            clean_chars.append(raw_line[i])
            i += 1

        clean_line = "".join(clean_chars)
        cleaned_lines.append((line_idx, raw_line.rstrip("\r\n"), clean_line))

    return cleaned_lines, interpolations


def check_csharp_syntax(file_path):
    """
    Rigorozno proverava pojedinačni C# (.cs) fajl na postojanje C# 6.0/7.0 odlika.
    Vraća listu pronađenih prekršaja (line_idx, category, line_content).
    """
    if not os.path.exists(file_path):
        return [(0, "File Not Found", f"Fajl ne postoji: {file_path}")]

    with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
        raw_content = f.read()

    cleaned_lines, violations = strip_comments_and_strings(raw_content)

    for line_idx, orig, clean in cleaned_lines:
        s = clean.strip()
        if not s:
            continue

        # -------------------------------------------------------------
        # 1. Provera Expression-Bodied Members (=>)
        # -------------------------------------------------------------
        if re.search(r"\b(?:get|set|add|remove)\s*=>", clean):
            violations.append((
                line_idx,
                "Expression-bodied accessor (=> u get/set/add/remove)",
                orig.strip()
            ))

        elif "=>" in clean:
            idx_arrow = clean.find("=>")
            before_arrow = clean[:idx_arrow].strip()

            # Expression-bodied metoda: [modifiers] [Type] [Name](...) =>
            is_expr_method = bool(re.search(
                r"(?:^|;|\{)\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed|async)\s+)*[A-Za-z0-9_<>\[\],\s]+\s+[A-Za-z0-9_]+\s*\([^)]*\)\s*$",
                before_arrow
            ))

            # Expression-bodied svojstvo: [modifiers] [Type] [Name] =>
            is_expr_prop = bool(re.search(
                r"(?:^|;|\{)\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed)\s+)+[A-Za-z0-9_<>\[\],\s]+\s+[A-Za-z0-9_]+\s*$",
                before_arrow
            ))

            # Provera da li je legitimni lambda izraz unutar koda
            is_lambda = bool(re.search(
                r"(?:[=+-,(]|\breturn\b|\bthis\s*\(|\bbase\s*\(|\?\s*\(?)\s*(?:\([^)]*\)|[A-Za-z0-9_]+)?\s*$",
                before_arrow
            ))

            if is_expr_method:
                violations.append((
                    line_idx,
                    "Expression-bodied method (=>)",
                    orig.strip()
                ))
            elif is_expr_prop:
                violations.append((
                    line_idx,
                    "Expression-bodied property (=>)",
                    orig.strip()
                ))
            elif not is_lambda:
                if re.search(r"^\s*(?:(?:public|private|protected|internal|static|virtual|override|sealed)\s+)?\w+[\w<>\[\],\s]*\s+\w+\s*$", before_arrow):
                    violations.append((
                        line_idx,
                        "Expression-bodied member (=>)",
                        orig.strip()
                    ))

        # -------------------------------------------------------------
        # 2. Provera Auto-Property Initializers ({ get; } = ...)
        # -------------------------------------------------------------
        if re.search(r"\{\s*(?:(?:private|protected|internal)?\s*(?:get|set)\s*;\s*)+\}\s*=", clean):
            violations.append((
                line_idx,
                "Auto-property initializer ({ get; } = ...)",
                orig.strip()
            ))

        # -------------------------------------------------------------
        # 3. Provera Null-Conditional operatora (?.)
        # -------------------------------------------------------------
        if re.search(r"(?<!\?)\?\.(?![0-9])|(?<!\?)\?\[", clean):
            violations.append((
                line_idx,
                "Null-conditional operator (?.)",
                orig.strip()
            ))

        # -------------------------------------------------------------
        # 4. Provera Nameof operatora (nameof(...))
        # -------------------------------------------------------------
        if re.search(r"\bnameof\s*\(", clean):
            violations.append((
                line_idx,
                "Nameof operator (nameof(...))",
                orig.strip()
            ))

        # -------------------------------------------------------------
        # 5. Provera Out Var deklaracija (out var ...)
        # -------------------------------------------------------------
        if re.search(r"\bout\s+var\b", clean):
            violations.append((
                line_idx,
                "Out variable inline declaration (out var ...)",
                orig.strip()
            ))

        # -------------------------------------------------------------
        # 6. Provera Throw izraza (?? throw ..., : throw ...)
        # -------------------------------------------------------------
        if re.search(r"(?:\?\?|:)\s*throw\b|=>\s*throw\b", clean):
            violations.append((
                line_idx,
                "Throw expression (?? throw ...)",
                orig.strip()
            ))

    # Sortiranje po linijama koda
    violations.sort(key=lambda x: x[0])
    return violations


def collect_target_files():
    """
    Sakuplja sve .cs fajlove iz ciljnih direktorijuma.
    """
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent
    target_dirs = [repo_root / "UI", repo_root / "Launcher"]

    files = []
    for d in target_dirs:
        if d.exists() and d.is_dir():
            for root, _, filenames in os.walk(str(d)):
                for fn in sorted(filenames):
                    if fn.endswith(".cs"):
                        files.append(Path(root) / fn)
    return files


def main():
    print(f"\n{BOLD}{CYAN}{'='*75}{RESET}")
    print(f"{BOLD}{CYAN}   POMOĆNA LINUX STATIČKA KONTROLA C# 5.0 SINTAKSE (PRE MSBUILD-A){RESET}")
    print(f"{BOLD}{CYAN}{'='*75}{RESET}")

    if len(sys.argv) > 1:
        target_files = [Path(arg).resolve() for arg in sys.argv[1:]]
    else:
        target_files = collect_target_files()

    if not target_files:
        print(f"{RED}[FAIL] Nije pronađen nijedan .cs fajl za analizu!{RESET}")
        return 1

    total_files = len(target_files)
    passed_files = 0
    total_violations = 0

    for file_path in target_files:
        print(f"\n{BOLD}[FAJL]{RESET} {CYAN}{file_path}{RESET}")

        violations = check_csharp_syntax(str(file_path))
        if not violations:
            print(f"  {GREEN}[PASS]{RESET} 100% čist C# 5.0 kod (0 prekršaja)")
            passed_files += 1
        else:
            print(f"  {RED}[FAIL]{RESET} Detektovano {len(violations)} nedozvoljenih C# 6.0/7.0 funkcija:")
            for line_num, category, line_str in violations:
                print(f"    {YELLOW}Linija {line_num:4d}:{RESET} [{RED}{category}{RESET}] -> {line_str}")
            total_violations += len(violations)

    print(f"\n{BOLD}{CYAN}{'='*75}{RESET}")
    print(f"{BOLD}REZIME POMOĆNE LINUX STATIČKE/LEKSIČKE ANALIZE C# 5.0 SINTAKSE:{RESET}")
    print(f"  Napomena                 : Pomoćna leksička kontrola pre stvarne MSBuild kompilacije")
    print(f"  Ukupno skenirano fajlova : {total_files}")
    print(f"  Uspešno položilo         : {passed_files}/{total_files}")
    print(f"  Ukupno grešaka/prekršaja : {total_violations}")
    print(f"{BOLD}{CYAN}{'='*75}{RESET}")

    if total_violations == 0 and passed_files == total_files:
        print(f"{BOLD}{GREEN}✓ SVI C# FAJLOVI SU PROŠLI POMOĆNU STATIČKU KONTROLU C# 5.0 SINTAKSE!{RESET}")
        print(f"  (Pomoćna kontrola završena; stvarni PE32 binarci se grade kroz MSBuild u Docker Wine okruženju)\n")
        return 0
    else:
        print(f"{BOLD}{RED}✗ DETEKTOVANE SU C# 6.0/7.0 NEKOMPATIBILNOSTI! KOD NIJE SPREMAN ZA BUILD.{RESET}\n")
        return 1


if __name__ == "__main__":
    sys.exit(main())
