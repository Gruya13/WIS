#!/usr/bin/env python3
"""Generiše razvojne verzije iz root version.json; ne menja binarce ili produkciju."""
import argparse
import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
COMPONENTS = ('iso', 'launcher', 'ui', 'servicehub')

# Kanonski parovi deljenih skripti i konfiguracionih datoteka (Single Source of Truth)
SHARED_SYNC_GROUPS = (
    ('ISO/FirstLogon.cmd', ('ISO/Skripte/FirstLogon.cmd', 'ISO/media/oem/Setup/Scripts/FirstLogon.cmd')),
    ('ISO/FirstLogon.ps1', ('ISO/Skripte/FirstLogon.ps1', 'ISO/media/oem/Setup/Scripts/FirstLogon.ps1')),
    ('ISO/SetupComplete.ps1', ('ISO/Skripte/SetupComplete.ps1', 'ISO/media/oem/Setup/Scripts/SetupComplete.ps1')),
    ('ISO/SetupComplete.cmd', ('ISO/media/oem/Setup/Scripts/SetupComplete.cmd',)),
    ('UI/Skripte/Provera_autopilota.ps1', ('ISO/Provera_autopilota.ps1', 'ISO/Skripte/Provera_autopilota.ps1')),
    ('ISO/WIS-ConnectNAS.cmd', ('ISO/Skripte/WIS-ConnectNAS.cmd', 'ISO/media/oem/Setup/Scripts/WIS-ConnectNAS.cmd')),
    ('ISO/WIS-AcceleratePE.cmd', ('ISO/Skripte/WIS-AcceleratePE.cmd', 'ISO/media/oem/Setup/Scripts/WIS-AcceleratePE.cmd')),
    ('Wifi-Servis.example.ini', ('ISO/Wifi-Servis.example.ini',)),
    ('Launcher/trusted_public_key.xml', ('ServiceHub/trusted_public_key.xml',)),
)


def load_versions(root):
    data = json.loads((Path(root) / 'version.json').read_text(encoding='utf-8'))
    versions = {name: data['components'][name]['version'] for name in COMPONENTS}
    for value in [data['version'], *versions.values(), data['compatibility']['min_launcher_version']]:
        if not isinstance(value, str) or not re.fullmatch(r'\d+\.\d+\.\d+', value):
            raise ValueError('Svaka verzija mora biti numerički SemVer.')
    if data['release_package']['version'] != data['version']:
        raise ValueError('Oznaka WIS paketa se ne poklapa sa root verzijom.')
    if tuple(map(int, versions['launcher'].split('.'))) < tuple(map(int, data['compatibility']['min_launcher_version'].split('.'))):
        raise ValueError('Dodeljeni Launcher je stariji od zahtevanog minimuma.')
    if data['components']['iso']['embedded_launcher_version'] != versions['launcher']:
        raise ValueError('ISO mora navesti stvarno planiranu Launcher verziju.')
    return data


def expected_files(root):
    root = Path(root)
    data = load_versions(root)
    result = {}
    for name, folder in [('ui', 'UI'), ('launcher', 'Launcher')]:
        version = data['components'][name]['version']
        p = Path(folder) / 'Properties/AssemblyInfo.cs'
        s = (root / p).read_text(encoding='utf-8')
        s = re.sub(r'(Assembly(?:File)?Version\(")[^"]+("\))', lambda m: m[1] + version + '.0' + m[2], s)
        s = re.sub(r'(AssemblyInformationalVersion\(")[^"]+("\))', lambda m: m[1] + version + m[2], s)
        result[p] = s
    p = Path('UI/LocalizationService.cs')
    if (root / p).is_file():
        ui_ver = data['components']['ui']['version']
        loc_src = (root / p).read_text(encoding='utf-8')
        loc_src = re.sub(
            r'(_(?:sr|en|zh)Dict\["AppTitle"\]\s*=\s*"WIS v)\d+\.\d+\.\d+',
            lambda m: m[1] + ui_ver,
            loc_src,
        )
        result[p] = loc_src
    ui_ver = data['components']['ui']['version']
    p_hw_res = Path('UI/HwTest/Core/ResultModel.cs')
    if (root / p_hw_res).is_file():
        rm_src = (root / p_hw_res).read_text(encoding='utf-8')
        rm_src = re.sub(
            r'(AppVersion\s*=\s*")\d+\.\d+\.\d+(";)',
            lambda m: m[1] + ui_ver + m[2],
            rm_src,
        )
        result[p_hw_res] = rm_src
    p_hw_bld = Path('UI/HwTest/Build-HwTest.py')
    if (root / p_hw_bld).is_file():
        hb_src = (root / p_hw_bld).read_text(encoding='utf-8')
        hb_src = re.sub(
            r'(Assembly(?:File)?Version\(\\")\d+\.\d+\.\d+\.\d+(\\"\))',
            lambda m: m[1] + ui_ver + '.0' + m[2],
            hb_src,
        )
        result[p_hw_bld] = hb_src
    p = Path('Launcher/PayloadManager.cs')
    s = (root / p).read_text(encoding='utf-8')
    launcher = data['components']['launcher']['version']
    s = re.sub(r'public const string LauncherVersion = "[^"]+";', 'public const string LauncherVersion = "WIS Launcher v' + launcher + '";', s)
    s = re.sub(r'public const string CurrentLauncherSemVer = "[^"]+";', 'public const string CurrentLauncherSemVer = "' + launcher + '";', s)
    result[p] = s
    result[Path('ServiceHub/servishub/version.py')] = ('"""Generisano iz WIS root version.json; ne menjati ručno."""\n'
        + 'VERSION = ' + repr(data['components']['servicehub']['version']) + '\n'
        + 'WIS_RELEASE_VERSION = ' + repr(data['version']) + '\n'
        + 'RELEASE_STATUS = ' + repr(data['release_status']) + '\n')
    for folder, name in [('UI', 'ui'), ('Launcher', 'launcher'), ('ServiceHub', 'servicehub')]:
        component = dict(data, name='wis-' + name, version=data['components'][name]['version'],
                         wis_release_version=data['version'], generated_from='../version.json')
        result[Path(folder) / 'version.json'] = json.dumps(component, ensure_ascii=False, indent=2) + '\n'
    for p in ['ISO/version.json', 'ISO/Skripte/version.json']:
        result[Path(p)] = json.dumps(data, ensure_ascii=False, indent=2) + '\n'
    for src_rel, targets in SHARED_SYNC_GROUPS:
        src_path = root / src_rel
        if src_path.is_file():
            raw_bytes = src_path.read_bytes()
            for tgt_rel in targets:
                result[Path(tgt_rel)] = raw_bytes
    return result


def sync_versions(root, check=False):
    mismatches = []
    for rel, expected in expected_files(root).items():
        path = Path(root) / rel
        if isinstance(expected, bytes):
            is_diff = (not path.exists()) or (path.read_bytes() != expected)
            if is_diff:
                mismatches.append(str(rel))
                if not check:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(expected)
        else:
            if not path.exists() or path.read_text(encoding='utf-8') != expected:
                mismatches.append(str(rel))
                if not check:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text(expected, encoding='utf-8')
    return mismatches


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    changes = sync_versions(ROOT, args.check)
    if args.check and changes:
        print('[FAIL] Neusaglašene verzije: ' + ', '.join(changes), file=sys.stderr)
        return 1
    print('[PASS] Verzije proverene.' if args.check else '[PASS] Generisano/ažurirano: ' + str(len(changes)) + ' fajlova.')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ValueError, KeyError, OSError) as error:
        print('[FAIL] ' + str(error), file=sys.stderr)
        sys.exit(1)
