"""
servishub.cli
=============
CLI interfejs za konfiguraciju i pokretanje ServisHub servera.
"""

import argparse
from servishub.config import active_config
from servishub.server import run_hub_server


def main():
    parser = argparse.ArgumentParser(
        description="WIS Service Centralni Servisni Hub (ServisHub v3.1)",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument(
        "--host",
        default=active_config.host,
        help="IP adresa na kojoj server osluškuje (0.0.0.0 za sve mrežne interfejse)",
    )
    parser.add_argument(
        "-p", "--port",
        type=int,
        default=active_config.port,
        help="HTTP port za REST API i Web Dashboard",
    )
    parser.add_argument(
        "--udp-port",
        type=int,
        default=active_config.udp_port,
        help="UDP port za Auto-Discovery osluškivač",
    )
    parser.add_argument(
        "--root", "--base-dir",
        dest="base_dir",
        default=active_config.base_dir,
        help="Koreni radni direktorijum projekta",
    )
    parser.add_argument(
        "--storage-dir",
        default=active_config.storage_dir,
        help="Direktorijum za skladište (keš drajvera i payload-a)",
    )
    parser.add_argument(
        "--logs-dir",
        default=active_config.logs_dir,
        help="Direktorijum za arhivu logova i servisnih kartona",
    )
    parser.add_argument(
        "--payload-dir",
        default=active_config.payload_source_dir,
        help="Izvorni direktorijum za dinamičko pakovanje OTA arhive",
    )
    parser.add_argument(
        "--updates-dir",
        default=active_config.updates_dir,
        help="Direktorijum skladišta za Windows 11 kumulativna ažuriranja (.msu/.cab)",
    )

    args, unknown = parser.parse_known_args()

    # Očuvanje unazadne kompatibilnosti ako je port prosleđen kao pozicioni argument
    if unknown:
        for u in unknown:
            if u.isdigit():
                args.port = int(u)
                break

    run_hub_server(
        host=args.host,
        port=args.port,
        udp_port=args.udp_port,
        base_dir=args.base_dir,
        storage_dir=args.storage_dir,
        logs_dir=args.logs_dir,
        payload_source_dir=args.payload_dir,
        updates_dir=args.updates_dir,
    )


if __name__ == "__main__":
    main()
