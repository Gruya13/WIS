#!/usr/bin/env python3
"""
Generise A4 PDF dokument sa dva identicna QR koda ("WIS-CAMTEST-v1") za testiranje
kamere u WIS.HwTest.exe, sa natpisom "WIS" u sredini svakog QR koda i linijom za secenje.
"""

import math
import os
import subprocess
import sys
import cairo

QR_PAYLOAD = "WIS-CAMTEST-v1"
OUTPUT_PDF = os.path.join(os.path.dirname(os.path.abspath(__file__)), "WIS_Camera_QR_Test_A4.pdf")

# A4 dimenzije u PDF tackama (1 pt = 1/72 incha)
A4_WIDTH = 595.2756
A4_HEIGHT = 841.8898


def get_qr_matrix(payload):
    """Poziva qrencode -l H -t ASCII -m 0 i vraca 2D listu bool vrednosti."""
    res = subprocess.run(
        ["qrencode", "-l", "H", "-t", "ASCII", "-m", "0", payload],
        capture_output=True,
        text=True,
        check=True,
    )
    lines = [line.rstrip("\r\n") for line in res.stdout.splitlines() if line.strip()]
    if not lines:
        raise RuntimeError("qrencode nije vratio QR matricu.")
    matrix = []
    for line in lines:
        row = []
        for i in range(0, len(line), 2):
            pair = line[i : i + 2]
            row.append(pair == "##")
        matrix.append(row)
    return matrix


def draw_rounded_rect(ctx, x, y, w, h, r):
    ctx.new_sub_path()
    ctx.arc(x + w - r, y + r, r, -math.pi / 2, 0)
    ctx.arc(x + w - r, y + h - r, r, 0, math.pi / 2)
    ctx.arc(x + r, y + h - r, r, math.pi / 2, math.pi)
    ctx.arc(x + r, y + r, r, math.pi, 3 * math.pi / 2)
    ctx.close_path()


def draw_centered_text(ctx, text, cx, cy, font_family, font_size, bold=False, color=(0.1, 0.12, 0.15)):
    weight = cairo.FONT_WEIGHT_BOLD if bold else cairo.FONT_WEIGHT_NORMAL
    ctx.select_font_face(font_family, cairo.FONT_SLANT_NORMAL, weight)
    ctx.set_font_size(font_size)
    ext = ctx.text_extents(text)
    tx = cx - (ext.width / 2.0 + ext.x_bearing)
    ty = cy - (ext.height / 2.0 + ext.y_bearing)
    ctx.set_source_rgb(*color)
    ctx.move_to(tx, ty)
    ctx.show_text(text)


def draw_qr_card(ctx, matrix, card_top, card_height, card_index):
    n = len(matrix)
    cx = A4_WIDTH / 2.0

    # Okvir kartice sa blagom marginom
    card_margin_x = 34.0
    card_margin_y = 18.0
    box_x = card_margin_x
    box_y = card_top + card_margin_y
    box_w = A4_WIDTH - 2 * card_margin_x
    box_h = card_height - 2 * card_margin_y

    draw_rounded_rect(ctx, box_x, box_y, box_w, box_h, 12.0)
    ctx.set_source_rgb(1.0, 1.0, 1.0)
    ctx.fill_preserve()
    ctx.set_source_rgb(0.80, 0.83, 0.88)
    ctx.set_line_width(1.0)
    ctx.stroke()

    # Zaglavlje kartice
    draw_centered_text(
        ctx,
        "TEHNO KUTAK — WIS TEST KAMERE (#{0})".format(card_index),
        cx,
        box_y + 21.0,
        "Sans",
        13.5,
        bold=True,
        color=(0.08, 0.11, 0.16),
    )
    draw_centered_text(
        ctx,
        "Usmerite QR kod u okvir na ekranu tokom testa kamere (WIS.HwTest.exe)",
        cx,
        box_y + 38.0,
        "Sans",
        9.5,
        bold=False,
        color=(0.35, 0.40, 0.48),
    )

    # Velicina QR koda sa quiet-zone (3 modula sa svake strane za pouzdano skeniranje)
    qr_draw_size = 230.0
    module_size = qr_draw_size / float(n)
    qr_x = cx - qr_draw_size / 2.0
    qr_y = box_y + 78.0

    quiet = module_size * 3.0
    draw_rounded_rect(
        ctx,
        qr_x - quiet,
        qr_y - quiet,
        qr_draw_size + 2 * quiet,
        qr_draw_size + 2 * quiet,
        6.0,
    )
    ctx.set_source_rgb(1.0, 1.0, 1.0)
    ctx.fill_preserve()
    ctx.set_source_rgb(0.88, 0.90, 0.93)
    ctx.set_line_width(0.8)
    ctx.stroke()

    # Crtanje QR modula (vektorski, 100% ostro)
    ctx.set_source_rgb(0.0, 0.0, 0.0)
    for r in range(n):
        for c in range(n):
            if matrix[r][c]:
                # Blago preklapanje od 0.05 pt da PDF vieweri ne prikazu tanke bele linije izmedju kvadrata
                ctx.rectangle(
                    qr_x + c * module_size,
                    qr_y + r * module_size,
                    module_size + 0.05,
                    module_size + 0.05,
                )
    ctx.fill()

    # Centralna plocica sa tekstom "WIS" (unutar 30% tolerancije Level H ECC)
    # 7.6 x 4.8 modula zauzima < 6% povrsine QR matrice i ne dira finder/timing/format module
    badge_w = module_size * 7.6
    badge_h = module_size * 4.8
    badge_x = cx - badge_w / 2.0
    badge_y = (qr_y + qr_draw_size / 2.0) - badge_h / 2.0

    # Beli zastitni obrub oko plocice radi lakseg odvajanja modula
    pad = module_size * 0.45
    draw_rounded_rect(
        ctx,
        badge_x - pad,
        badge_y - pad,
        badge_w + 2 * pad,
        badge_h + 2 * pad,
        8.0,
    )
    ctx.set_source_rgb(1.0, 1.0, 1.0)
    ctx.fill()

    # Tamno-plava/crna plocica sa belim tekstom WIS ili bela plocica sa tamnim okvirom i WIS tekstom
    # Bela pozadina + tamni okvir daje najveci kontrast i ne stvara tamne artefakte skeneru
    draw_rounded_rect(ctx, badge_x, badge_y, badge_w, badge_h, 6.0)
    ctx.set_source_rgb(1.0, 1.0, 1.0)
    ctx.fill_preserve()
    ctx.set_source_rgb(0.05, 0.25, 0.45)
    ctx.set_line_width(2.2)
    ctx.stroke()

    draw_centered_text(
        ctx,
        "WIS",
        cx,
        qr_y + qr_draw_size / 2.0,
        "Sans",
        24.0,
        bold=True,
        color=(0.05, 0.18, 0.35),
    )

    # Podnozje kartice ispod quiet-zone okvira
    footer_y = box_y + box_h - 16.0
    draw_centered_text(
        ctx,
        "Payload: {0}   |   ECC: Level H (30%)   |   Format: A4 (2x kartica)".format(QR_PAYLOAD),
        cx,
        footer_y,
        "Sans",
        8.5,
        bold=False,
        color=(0.40, 0.45, 0.52),
    )


def generate_pdf(output_path=OUTPUT_PDF):
    matrix = get_qr_matrix(QR_PAYLOAD)
    surface = cairo.PDFSurface(output_path, A4_WIDTH, A4_HEIGHT)
    ctx = cairo.Context(surface)

    # Bela pozadina stranice
    ctx.set_source_rgb(1.0, 1.0, 1.0)
    ctx.rectangle(0, 0, A4_WIDTH, A4_HEIGHT)
    ctx.fill()

    half_h = A4_HEIGHT / 2.0
    draw_qr_card(ctx, matrix, 0.0, half_h, 1)
    draw_qr_card(ctx, matrix, half_h, half_h, 2)

    # Isprekidana linija za secenje po sredini A4 papira
    ctx.save()
    ctx.set_source_rgb(0.55, 0.58, 0.63)
    ctx.set_line_width(0.9)
    ctx.set_dash([6.0, 5.0])
    ctx.move_to(18.0, half_h)
    ctx.line_to(A4_WIDTH - 18.0, half_h)
    ctx.stroke()
    ctx.restore()

    # Bela pozadina iza natpisa za secenje
    ctx.set_source_rgb(1.0, 1.0, 1.0)
    ctx.rectangle(A4_WIDTH / 2.0 - 120.0, half_h - 8.0, 240.0, 16.0)
    ctx.fill()

    draw_centered_text(
        ctx,
        "Iseci po isprekidanoj liniji (2x servisna QR kartica)",
        A4_WIDTH / 2.0,
        half_h,
        "Sans",
        8.0,
        bold=False,
        color=(0.45, 0.48, 0.54),
    )

    surface.finish()
    return output_path, len(matrix)


if __name__ == "__main__":
    out, dim = generate_pdf()
    print("[+] Generisan PDF: {0} (QR matrica {1}x{1})".format(out, dim))
