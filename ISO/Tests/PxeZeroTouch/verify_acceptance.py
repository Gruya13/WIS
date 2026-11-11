#!/usr/bin/env python3
"""Provera zamrznutog ugovora i kompletnosti dokaza; ne izvršava WinPE testove."""
import argparse
import hashlib
import json
from pathlib import Path
import sys


class Invalid(ValueError):
    pass


def read_json(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise Invalid('Dupliran JSON ključ: ' + key)
            result[key] = value
        return result
    return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=pairs)


def contained_file(base, name):
    if not isinstance(name, str) or not name or Path(name).is_absolute():
        raise Invalid('Očekivana je neprazna relativna putanja.')
    path = (base / name).resolve()
    try:
        path.relative_to(base.resolve())
    except ValueError:
        raise Invalid('Putanja izlazi iz osnovnog direktorijuma: ' + name)
    if not path.is_file() or path.stat().st_size == 0:
        raise Invalid('Fajl nedostaje ili je prazan: ' + name)
    return path


def check_lock(base):
    lock = read_json(base / 'LOCK.json')
    if lock.get('schema_version') != 1 or not isinstance(lock.get('files'), dict):
        raise Invalid('Nepodržan LOCK format.')
    files = lock['files']
    required = {'acceptance.json', 'verify_acceptance.py', 'IMPLEMENTATION_PLAN.md'}
    if not required.issubset(files):
        raise Invalid('LOCK mora obuhvatiti matricu, validator i plan.')
    for name, expected in files.items():
        path = contained_file(base, name)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if not isinstance(expected, str) or expected != digest:
            raise Invalid('SHA-256 nepodudaranje: ' + name)
    return read_json(base / 'acceptance.json')


def check_results(spec, results_path):
    report = read_json(results_path)
    if report.get('suite') != spec.get('suite'):
        raise Invalid('Pogrešna oznaka test paketa.')
    metadata = report.get('run_metadata')
    fields = ('iso_sha256', 'machine', 'winpe_version', 'setup_version', 'iventoy_version', 'date')
    if not isinstance(metadata, dict) or any(not isinstance(metadata.get(k), str) or not metadata[k].strip() for k in fields):
        raise Invalid('Nedostaju metapodaci o izvršavanju.')
    iso_hash = metadata['iso_sha256']
    if len(iso_hash) != 64 or any(c not in '0123456789abcdefABCDEF' for c in iso_hash):
        raise Invalid('ISO SHA-256 mora imati 64 heksadecimalna znaka.')
    scenarios = spec['scenarios']
    expected = {item['id'] for item in scenarios}
    if len(expected) != len(scenarios):
        raise Invalid('Dupliran scenario u matrici.')
    results = report.get('results')
    if not isinstance(results, list):
        raise Invalid('results mora biti lista.')
    seen = set()
    for item in results:
        if not isinstance(item, dict):
            raise Invalid('Rezultat mora biti objekat.')
        sid = item.get('id')
        if not isinstance(sid, str) or sid not in expected:
            raise Invalid('Nepoznat scenario: ' + str(sid))
        if sid in seen:
            raise Invalid('Dupliran scenario: ' + sid)
        seen.add(sid)
        if item.get('status') != 'PASS':
            raise Invalid(sid + ': nije PASS (' + str(item.get('status')) + ').')
        evidence = item.get('evidence')
        if not isinstance(evidence, list) or not evidence:
            raise Invalid(sid + ': nema dokaza.')
        for name in evidence:
            contained_file(results_path.parent, name)
    missing = expected - seen
    if missing:
        raise Invalid('Nedostaju scenariji: ' + ', '.join(sorted(missing)))
    return len(seen)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-lock', action='store_true', help='Samo integritet; nije funkcionalni PASS.')
    parser.add_argument('--results', type=Path, help='JSON sa rezultatima i relativnim putanjama dokaza.')
    args = parser.parse_args()
    if args.check_lock and args.results:
        parser.error('--check-lock i --results se koriste odvojeno.')
    try:
        spec = check_lock(Path(__file__).resolve().parent)
        if args.check_lock:
            print('INTEGRITY_PASS — hash ugovora je ispravan; funkcionalni testovi nisu potvrđeni.')
            return 0
        if args.results is None:
            print('NOT_RUN — rezultati nisu dostavljeni; funkcionalni gate nije prošao.')
            return 2
        count = check_results(spec, args.results.resolve())
        print('EVIDENCE_COMPLETE — %d/%d prijava PASS sa nepraznim fajlovima dokaza. '
              'Ovo nije potvrda sadržaja logova; QA mora pregledati dokaze.' % (count, count))
        return 0
    except (Invalid, OSError, ValueError, KeyError, TypeError) as exc:
        print('GATE_FAILED — ' + str(exc), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
