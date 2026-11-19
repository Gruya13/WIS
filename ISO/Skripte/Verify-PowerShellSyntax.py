#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Verify-PowerShellSyntax.py - Rigorozna statička i sintaksna analiza PowerShell 5.1 koda
Verifikuje:
1. Leksički balans svih tipova zagrada: (), {}, []
2. Balans i regularnost jednostrukih i dvostrukih navodnika
3. Regularnost i zatvaranje blok komentara <# ... #>
4. Regularnost here-stringova (@'...'@ i @"..."@)
5. Pravilnu strukturu funkcija i blokova
6. PowerShell 5.1 kompatibilnost ključnih reči (nema nevalidnih 'return <statement>')
7. PowerShell 5.1 validnost varijabli (nema nevalidnih '$var:' konstrukcija bez važećeg scope ili drive prefiksa)
8. Detekciju duplikata ključeva u hash tabelama (@{ ... } i [ordered]@{ ... })
"""

import sys
import os
import re

def find_duplicate_hashtable_keys(file_path):
    """
    Rigorozna analiza PowerShell hash tabela (@{ ... } i [ordered]@{ ... })
    sa detekcijom višestruko navedenih ključeva (case-insensitive).
    """
    with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
        text = f.read()

    errors = []
    i = 0
    n = len(text)
    line_num = 1
    col_num = 1

    in_block_comment = False
    in_here_single = False
    in_here_double = False
    in_single_quote = False
    in_double_quote = False

    clean_chars = []

    while i < n:
        ch = text[i]
        curr_line = line_num
        curr_col = col_num

        if ch == '\n':
            line_num += 1
            col_num = 1
        else:
            col_num += 1

        if in_block_comment:
            if ch == '#' and i + 1 < n and text[i+1] == '>':
                in_block_comment = False
                i += 2
                col_num += 1
                continue
            i += 1
            continue

        if not (in_here_single or in_here_double or in_single_quote or in_double_quote):
            if ch == '<' and i + 1 < n and text[i+1] == '#':
                in_block_comment = True
                i += 2
                col_num += 1
                continue
            if ch == '#':
                while i < n and text[i] != '\n':
                    i += 1
                    col_num += 1
                continue

        if in_here_single:
            if ch == '\n' and i + 2 < n and text[i+1:i+3] == "'@":
                in_here_single = False
                i += 3
                line_num += 1
                col_num = 1
                continue
            i += 1
            continue

        if in_here_double:
            if ch == '\n' and i + 2 < n and text[i+1:i+3] == '"@':
                in_here_double = False
                i += 3
                line_num += 1
                col_num = 1
                continue
            i += 1
            continue

        if not (in_single_quote or in_double_quote):
            if ch == '@' and i + 1 < n:
                if text[i+1] == "'":
                    rest = text[i+2:i+20].split('\n', 1)
                    if len(rest) > 1 and rest[0].strip() == '':
                        in_here_single = True
                        i += 2 + len(rest[0]) + 1
                        line_num += 1
                        col_num = 1
                        continue
                elif text[i+1] == '"':
                    rest = text[i+2:i+20].split('\n', 1)
                    if len(rest) > 1 and rest[0].strip() == '':
                        in_here_double = True
                        i += 2 + len(rest[0]) + 1
                        line_num += 1
                        col_num = 1
                        continue

        if in_single_quote:
            if ch == "'":
                if i + 1 < n and text[i+1] == "'":
                    i += 2
                    col_num += 1
                    continue
                in_single_quote = False
                clean_chars.append(("'", curr_line, curr_col))
                i += 1
                continue
            clean_chars.append((ch, curr_line, curr_col))
            i += 1
            continue

        if in_double_quote:
            if ch == '`' and i + 1 < n:
                i += 2
                col_num += 1
                continue
            if ch == '"':
                in_double_quote = False
                clean_chars.append(('"', curr_line, curr_col))
                i += 1
                continue
            clean_chars.append((ch, curr_line, curr_col))
            i += 1
            continue

        if ch == "'":
            in_single_quote = True
            clean_chars.append(("'", curr_line, curr_col))
            i += 1
            continue

        if ch == '"':
            in_double_quote = True
            clean_chars.append(('"', curr_line, curr_col))
            i += 1
            continue

        clean_chars.append((ch, curr_line, curr_col))
        i += 1

    stack = []
    idx = 0
    m = len(clean_chars)

    while idx < m:
        ch, ln, col = clean_chars[idx]

        if ch == '@' and idx + 1 < m and clean_chars[idx+1][0] == '{':
            stack.append({
                'type': 'HASH',
                'start_line': ln,
                'keys': {},
                'current_key_buffer': "",
                'saw_equals': False
            })
            idx += 2
            continue

        elif ch == '{':
            stack.append({
                'type': 'BLOCK',
                'start_line': ln,
                'keys': {},
                'current_key_buffer': "",
                'saw_equals': False
            })
            idx += 1
            continue

        elif ch == '}':
            if stack:
                stack.pop()
            idx += 1
            continue

        elif ch == '(':
            stack.append({'type': 'PAREN', 'start_line': ln})
            idx += 1
            continue

        elif ch == ')':
            if stack and stack[-1]['type'] == 'PAREN':
                stack.pop()
            idx += 1
            continue

        elif ch == '[':
            stack.append({'type': 'BRACKET', 'start_line': ln})
            idx += 1
            continue

        elif ch == ']':
            if stack and stack[-1]['type'] == 'BRACKET':
                stack.pop()
            idx += 1
            continue

        if stack and stack[-1]['type'] == 'HASH':
            top = stack[-1]
            if not top['saw_equals']:
                if ch == '=':
                    if idx + 1 < m and clean_chars[idx+1][0] == '=':
                        top['current_key_buffer'] += '=='
                        idx += 2
                        continue
                    raw_key = top['current_key_buffer'].strip()
                    clean_key = raw_key
                    if (clean_key.startswith("'") and clean_key.endswith("'")) or \
                       (clean_key.startswith('"') and clean_key.endswith('"')):
                        clean_key = clean_key[1:-1]
                    clean_key = clean_key.strip()
                    if clean_key:
                        key_norm = clean_key.lower()
                        if key_norm in top['keys']:
                            orig_key, orig_line = top['keys'][key_norm]
                            errors.append((ln, clean_key, orig_line, orig_key))
                        else:
                            top['keys'][key_norm] = (clean_key, ln)
                    top['saw_equals'] = True
                    top['current_key_buffer'] = ""
                elif ch in (';', '\n'):
                    top['current_key_buffer'] = ""
                else:
                    top['current_key_buffer'] += ch
            else:
                if ch in (';', '\n'):
                    top['saw_equals'] = False
                    top['current_key_buffer'] = ""

        idx += 1

    return errors

def analyze_ps_syntax(file_path):
    print(f"\n{'='*70}")
    print(f"   ANALIZA POWERSHELL SINTAKSE: {os.path.basename(file_path)}")
    print(f"   Putanja: {file_path}")
    print(f"{'='*70}")

    if not os.path.exists(file_path):
        print(f"  [FAIL] Datoteka ne postoji: {file_path}")
        return False

    with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
        lines = f.readlines()

    total_lines = len(lines)
    print(f"  Ukupno linija koda: {total_lines}")

    in_block_comment = False
    in_here_string_single = False
    in_here_string_double = False
    bracket_stack = []
    errors = []

    # PowerShell 5.1 specifična pravila za return
    invalid_returns = []
    # Nevalidne '$var:' konstrukcije u PS 5.1 (InvalidVariableReferenceWithDrive)
    invalid_var_colons = []
    # Pronađene funkcije
    found_functions = []

    for line_idx, line in enumerate(lines, 1):
        stripped = line.strip()

        # Provera nevalidnih 'return <statement>' konstrukcija u PS 5.1
        if re.search(r'^\s*return\s+(if|foreach|while|switch|try)\b', line):
            invalid_returns.append((line_idx, stripped))

        # Detekcija deklaracije funkcija
        fn_match = re.match(r'^\s*function\s+([A-Za-z0-9_-]+)', line, re.IGNORECASE)
        if fn_match:
            found_functions.append((line_idx, fn_match.group(1)))

        # Here-string stanja prema PowerShell pravilima:
        # Zatvaranje: linija MORA poceti sa '@ ili "@
        if in_here_string_single:
            if line.startswith("'@"):
                in_here_string_single = False
                # Nastavi obradu ostatka linije nakon '@
                line = line[2:]
            else:
                continue

        if in_here_string_double:
            if line.startswith('"@'):
                in_here_string_double = False
                # Nastavi obradu ostatka linije nakon "@
                line = line[2:]
            else:
                for vm in re.finditer(r'(?<!`)\$([a-zA-Z0-9_]+):', line):
                    scope = vm.group(1).lower()
                    rest = line[vm.end():]
                    is_drive = len(scope) == 1 and scope.isalpha() and (rest.startswith('\\') or rest.startswith('/'))
                    if scope not in ('global', 'script', 'local', 'private', 'env', 'using') and not is_drive:
                        invalid_var_colons.append((line_idx, vm.group(0), stripped))
                continue

        # Otvaranje: @' ili @" na kraju linije
        if re.search(r"@'\s*$", line):
            in_here_string_single = True
            # Obradi deo linije pre @'
            line = re.sub(r"@'\s*$", "", line)

        elif re.search(r'@"\s*$', line):
            in_here_string_double = True
            # Obradi deo linije pre @"
            line = re.sub(r'@"\s*$', "", line)

        # Leksička obrada karaktera u liniji
        i = 0
        n = len(line)
        in_sq = False
        in_dq = False

        while i < n:
            ch = line[i]

            # Blok komentar <# ... #>
            if in_block_comment:
                if ch == '#' and i + 1 < n and line[i+1] == '>':
                    in_block_comment = False
                    i += 2
                    continue
                i += 1
                continue

            if not in_sq and not in_dq:
                if ch == '<' and i + 1 < n and line[i+1] == '#':
                    in_block_comment = True
                    i += 2
                    continue
                if ch == '#':
                    # Linijski komentar - ostatak linije se zanemaruje
                    break

            # Jednostruki navodnici
            if ch == "'" and not in_dq:
                if in_sq and i + 1 < n and line[i+1] == "'":
                    i += 2
                    continue
                in_sq = not in_sq
                i += 1
                continue

            # Dvostruki navodnici
            if ch == '"' and not in_sq:
                if i > 0 and line[i-1] == '`':
                    i += 1
                    continue
                if in_dq and i + 1 < n and line[i+1] == '"':
                    i += 2
                    continue
                in_dq = not in_dq
                i += 1
                continue

            # Provera nevalidnih '$var:' konstrukcija (npr. "$pSub: " izaziva ParserError u PS 5.1)
            if ch == '$' and not in_sq:
                if i == 0 or line[i-1] != '`':
                    var_match = re.match(r'^\$([a-zA-Z0-9_]+):', line[i:])
                    if var_match:
                        scope = var_match.group(1).lower()
                        rest = line[i + var_match.end():]
                        is_drive = len(scope) == 1 and scope.isalpha() and (rest.startswith('\\') or rest.startswith('/'))
                        if scope not in ('global', 'script', 'local', 'private', 'env', 'using') and not is_drive:
                            invalid_var_colons.append((line_idx, var_match.group(0), stripped))

            # Unutar stringova zagrade se ignorišu
            if in_sq or in_dq:
                i += 1
                continue

            # Balans zagrada
            if ch in '({[':
                bracket_stack.append((ch, line_idx, i + 1))
            elif ch in ')}]':
                if not bracket_stack:
                    errors.append(f"Neočekivana zatvorena zagrada '{ch}' na liniji {line_idx}:{i+1}")
                else:
                    open_ch, open_line, open_col = bracket_stack.pop()
                    expected = {'(': ')', '{': '}', '[': ']'}[open_ch]
                    if ch != expected:
                        errors.append(f"Neusklađena zagrada: '{open_ch}' sa linije {open_line}:{open_col} zatvorena sa '{ch}' na liniji {line_idx}:{i+1}")

            i += 1

        if in_sq:
            errors.append(f"Nezatvoreni jednostruki navodnici na liniji {line_idx}")
        if in_dq:
            errors.append(f"Nezatvoreni dvostruki navodnici na liniji {line_idx}")

    if in_block_comment:
        errors.append("Nezatvoren blok komentar <# ... #>")
    if in_here_string_single or in_here_string_double:
        errors.append("Nezatvoren here-string")

    while bracket_stack:
        open_ch, open_line, open_col = bracket_stack.pop()
        errors.append(f"Nezatvorena zagrada '{open_ch}' sa linije {open_line}:{open_col}")

    # Provera duplikata ključeva u hash tabelama
    dup_keys = find_duplicate_hashtable_keys(file_path)

    # Prikaz rezultata analize
    print(f"  Detektovano funkcija   : {len(found_functions)}")
    print(f"  Nevalidnih 'return'    : {len(invalid_returns)}")
    print(f"  Nevalidnih '$var:'     : {len(invalid_var_colons)}")
    print(f"  Duplih ključeva mape   : {len(dup_keys)}")
    print(f"  Sintaksnih grešaka     : {len(errors)}")

    if invalid_returns:
        for r_line, r_code in invalid_returns:
            print(f"  [FAIL] Linija {r_line}: Nevalidan return u PS 5.1 -> {r_code}")
            errors.append(f"Invalid return on line {r_line}")

    if invalid_var_colons:
        for v_line, v_match, v_code in invalid_var_colons:
            print(f"  [FAIL] Linija {v_line}: Nevalidna '$var:' referenca -> '{v_match}' u: {v_code}")
            errors.append(f"Nevalidna '$var:' referenca '{v_match}' na liniji {v_line}")

    if dup_keys:
        for dk_line, dk_key, dk_orig_line, dk_orig_key in dup_keys:
            print(f"  [FAIL] Linija {dk_line}: Duplikat ključa '{dk_key}' (prethodno definisan na liniji {dk_orig_line} kao '{dk_orig_key}')")
            errors.append(f"Duplikat ključa '{dk_key}' na liniji {dk_line}")

    if errors:
        for err in errors[:10]:
            print(f"  [FAIL] {err}")
        return False
    else:
        print("  [PASS] Zagrade, navodnici, komentari i here-stringovi su 100% balansirani.")
        print("  [PASS] PowerShell 5.1 pravila za 'return' su 100% poštovana.")
        print("  [PASS] PowerShell 5.1 pravila za '$var:' reference su 100% poštovana.")
        print("  [PASS] Duplikati ključeva u hash tabelama: nema duplikata.")
        return True

def main():
    print("=" * 70)
    print(" [POMOĆNA STATIČKA KONTROLA] Linux leksička i sintaksna analiza PS 5.1")
    print(" (Pomoćna statička heuristika — ne zamenjuje Windows Parser.ParseFile)")
    print("=" * 70)

    proj_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if len(sys.argv) > 1:
        targets = [os.path.abspath(arg) for arg in sys.argv[1:]]
    else:
        modules_dir = os.path.join(proj_root, "..", "UI", "Skripte", "Modules")
        phases_dir = os.path.join(proj_root, "..", "UI", "Skripte", "Phases")
        targets = []
        if os.path.exists(modules_dir):
            for m in sorted(os.listdir(modules_dir)):
                if m.endswith((".psm1", ".ps1")):
                    targets.append(os.path.join(modules_dir, m))
        if os.path.exists(phases_dir):
            for p in sorted(os.listdir(phases_dir)):
                if p.endswith((".psm1", ".ps1")):
                    targets.append(os.path.join(phases_dir, p))
        for extra_rel in [
            "SetupComplete.ps1",
            "FirstLogon.ps1",
            os.path.join("sources", "$OEM$", "$$", "Setup", "Scripts", "PreOOBE-Network.ps1"),
        ]:
            extra_path = os.path.join(proj_root, extra_rel)
            if os.path.exists(extra_path):
                targets.append(extra_path)

    all_passed = True
    for t in targets:
        if not analyze_ps_syntax(t):
            all_passed = False

    print("\n" + "=" * 70)
    print(" [NAPOMENA] Ovo je pomoćna Linux statička i leksička kontrola")
    print("            (stvarna verifikacija AST stabla vrši se kroz PowerShell 5.1 Parser.ParseFile).")
    if all_passed:
        print(f" [REZULTAT] SVI FAJLOVI ({len(targets)}) PROŠLI SU POMOĆNU STATIČKU KONTROLU (0 GREŠAKA)!")
    else:
        print(" [REZULTAT] PRONAĐENE SU SINTAKSNE / LEKSIČKE GREŠKE!")
    print("=" * 70)
    return 0 if all_passed else 1

if __name__ == "__main__":
    sys.exit(main())
