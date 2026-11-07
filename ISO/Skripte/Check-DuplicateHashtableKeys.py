#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Check-DuplicateHashtableKeys.py
Skripta za rigoroznu detekciju duplih ključeva u PowerShell hash tabelama (@{ ... } i [ordered]@{ ... }).
Case-insensitive provera po pravilima PowerShell 5.1 / PowerShell Core.
"""

import sys
import os
import re

def check_file_duplicate_keys(file_path):
    with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
        text = f.read()

    lines = text.splitlines(keepends=True)
    
    # State tracking
    errors = []
    
    # We will tokenize text into characters with (char, line, col)
    # But first, handle comments, strings, here-strings
    tokens = []
    i = 0
    n = len(text)
    line_num = 1
    col_num = 1

    in_block_comment = False
    in_here_single = False
    in_here_double = False
    in_single_quote = False
    in_double_quote = False

    clean_chars = [] # list of (char, line_num, col_num)

    while i < n:
        ch = text[i]
        curr_line = line_num
        curr_col = col_num

        # Update line and column for next
        if ch == '\n':
            line_num += 1
            col_num = 1
        else:
            col_num += 1

        # 1. Block comments <# ... #>
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
                # Line comment: skip until newline
                while i < n and text[i] != '\n':
                    i += 1
                    col_num += 1
                continue

        # 2. Here-strings
        if in_here_single:
            if ch == '\n' and i + 2 < n and text[i+1:i+3] == "'@":
                # closing '@
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

        # Check for here-string open
        if not (in_single_quote or in_double_quote):
            if ch == '@' and i + 1 < n:
                if text[i+1] == "'":
                    # Check if followed by newline
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

        # 3. Regular strings
        if in_single_quote:
            if ch == "'":
                if i + 1 < n and text[i+1] == "'":
                    # Escaped single quote
                    i += 2
                    col_num += 1
                    continue
                in_single_quote = False
                clean_chars.append(("'", curr_line, curr_col))
                i += 1
                continue
            # Keep string chars as string token or just consume
            clean_chars.append((ch, curr_line, curr_col))
            i += 1
            continue

        if in_double_quote:
            if ch == '`' and i + 1 < n:
                # Escaped char
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

        # Normal code character
        clean_chars.append((ch, curr_line, curr_col))
        i += 1

    # Now we have clean_chars without comments and without here-strings.
    # Regular strings have delimiters ' and " preserved.
    # Next, we scan for hashtables: @{ ... }
    
    # We maintain a stack of contexts:
    # Each entry in stack:
    # {
    #   'type': 'HASH' | 'BLOCK' | 'PAREN' | 'BRACKET',
    #   'start_line': int,
    #   'keys': { key_lower: (orig_key, line_num) },  # only for HASH
    #   'current_key_buffer': str,
    #   'saw_equals': bool
    # }
    stack = []
    
    idx = 0
    m = len(clean_chars)
    
    while idx < m:
        ch, ln, col = clean_chars[idx]
        
        # Check for @{ opening
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
                closed = stack.pop()
            idx += 1
            continue
            
        elif ch == '(':
            stack.append({
                'type': 'PAREN',
                'start_line': ln
            })
            idx += 1
            continue
            
        elif ch == ')':
            if stack and stack[-1]['type'] == 'PAREN':
                stack.pop()
            idx += 1
            continue
            
        elif ch == '[':
            stack.append({
                'type': 'BRACKET',
                'start_line': ln
            })
            idx += 1
            continue
            
        elif ch == ']':
            if stack and stack[-1]['type'] == 'BRACKET':
                stack.pop()
            idx += 1
            continue

        # If inside a HASH and direct child (top of stack is HASH)
        if stack and stack[-1]['type'] == 'HASH':
            top = stack[-1]
            if not top['saw_equals']:
                # We are reading the key
                if ch == '=':
                    # In PowerShell, '==' or '-eq' is comparison, '=' is assignment
                    # Make sure it's not followed by another '='
                    if idx + 1 < m and clean_chars[idx+1][0] == '=':
                        # '==' operator! Ignore
                        top['current_key_buffer'] += '=='
                        idx += 2
                        continue
                    
                    # We found a key assignment!
                    raw_key = top['current_key_buffer'].strip()
                    # Clean key: remove quotes if present
                    clean_key = raw_key
                    if (clean_key.startswith("'") and clean_key.endswith("'")) or \
                       (clean_key.startswith('"') and clean_key.endswith('"')):
                        clean_key = clean_key[1:-1]
                    clean_key = clean_key.strip()
                    
                    if clean_key:
                        key_norm = clean_key.lower()
                        if key_norm in top['keys']:
                            orig_key, orig_line = top['keys'][key_norm]
                            errors.append({
                                'key': clean_key,
                                'line': ln,
                                'orig_key': orig_key,
                                'orig_line': orig_line,
                                'file': file_path
                            })
                        else:
                            top['keys'][key_norm] = (clean_key, ln)
                            
                    top['saw_equals'] = True
                    top['current_key_buffer'] = ""
                elif ch in (';', '\n'):
                    # Empty or reset before equals
                    top['current_key_buffer'] = ""
                else:
                    top['current_key_buffer'] += ch
            else:
                # We are reading the value until ';' or '\n'
                if ch in (';', '\n'):
                    top['saw_equals'] = False
                    top['current_key_buffer'] = ""

        idx += 1
        
    return errors

if __name__ == "__main__":
    targets = sys.argv[1:]
    total_errors = 0
    for t in targets:
        if os.path.isfile(t):
            errs = check_file_duplicate_keys(t)
            if errs:
                total_errors += len(errs)
                print(f"[FAIL] {t}: {len(errs)} duplikat(a) ključa!")
                for e in errs:
                    print(f"   -> Linija {e['line']}: Ključ '{e['key']}' je već definisan na liniji {e['orig_line']} (kao '{e['orig_key']}')")
            else:
                print(f"[PASS] {t}: Nema duplikata ključeva.")
    sys.exit(1 if total_errors > 0 else 0)
