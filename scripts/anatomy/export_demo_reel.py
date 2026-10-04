#!/usr/bin/env python3
"""Label synthetic Unity demo stills and reveal the existing pinned atlas export.

Run after OpenStepVisualsValidation.ExportDemo. Requires Pillow; no geometry,
scene, authored coordinates, or source atlas assets are modified.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
BG = (15, 24, 29)
TEXT = (239, 245, 244)
MUTED = (172, 193, 195)
MINT = (137, 226, 203)
PURPLE = (183, 157, 240)


def font(size: int, bold: bool = False):
    names = (["/System/Library/Fonts/Supplemental/Arial Bold.ttf", "DejaVuSans-Bold.ttf"]
             if bold else ["/System/Library/Fonts/Supplemental/Arial.ttf", "DejaVuSans.ttf"])
    for name in names:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    raise RuntimeError("Install Arial or DejaVu Sans to render legible demo labels")


def title(draw, eyebrow: str, heading: str, subtitle: str):
    draw.text((76, 54), eyebrow, font=font(23, True), fill=MINT)
    draw.text((74, 93), heading, font=font(52, True), fill=TEXT)
    draw.text((76, 160), subtitle, font=font(25), fill=MUTED)


def label_surgery(folder: Path):
    labels = {
        "0-mark": ("Mark the incision", "Measured marker path"),
        "1-skin": ("Open the skin", "Skin and subcutaneous fat"),
        "2-fascia": ("Open the fascia", "Aponeurosis divided along its fibers"),
        "3-muscle": ("Separate the muscle", "Paired retraction opens the muscle layer"),
        "4-tent-before-nick": ("Lift the membrane", "Synthetic fixture · tented before the nick"),
        "4-peritoneum": ("Open the peritoneum", "Membrane opened above the abdominal cavity"),
        "5-delivered": ("Deliver the appendix", "Synthetic instrument interaction lifts the tissue"),
        "6-clamped-before-cut": ("Clamp the vessels", "Synthetic fixture · clamps positioned before division"),
        "6-divided-before-ties": ("Divide the mesoappendix", "Synthetic fixture · division before ligatures"),
        "6-mesoappendix": ("Secure the vessels", "Ligatures and division of the mesoappendix"),
        "7-ligated-before-cut": ("Ligate the base", "Synthetic fixture · ligature positioned before division"),
        "7-base": ("Secure the base", "Ligated stump · specimen shown by assisted display"),
        "8-clean": ("Inspect the field", "Stump and vessel checks · assisted specimen display"),
        "9-closed": ("Close in layers", "Assisted closure · suture visibility overlay at original scale"),
        "closed": ("The operative field", "Before the synthetic demonstration"),
        "retry": ("Ready for another attempt", "Restored by the real retry path"),
    }
    for name, (heading, subtitle) in labels.items():
        path = folder / f"demo-step-{name}.png"
        if not path.exists():
            continue
        im = Image.open(path).convert("RGB")
        if im.size != (1920, 1080):
            raise ValueError(f"Expected 1920x1080: {path}")
        overlay = Image.new("RGBA", im.size)
        d = ImageDraw.Draw(overlay)
        d.rectangle((0, 0, 1920, 208), fill=(*BG, 255))
        d.rectangle((0, 1003, 1920, 1080), fill=(*BG, 255))
        title(d, "SCALPAL / OPEN APPENDECTOMY", heading, subtitle)
        d.text((76, 1025), "SYNTHETIC EDITOR DEMONSTRATION", font=font(23, True), fill=MINT)
        d.text((1150, 1025), "Teaching model · not headset footage", font=font(23), fill=MUTED)
        Image.alpha_composite(im.convert("RGBA"), overlay).convert("RGB").save(path)
        print(path)


def atlas_reveal(folder: Path, video: bool = False):
    source = Image.open(ROOT / "assets/anatomy/briefing-preview.png").convert("RGB")
    if source.size != (2100, 800):
        raise ValueError("Atlas preview shape changed; review the panel crops")
    panels = [source.crop((i * 700, 0, (i + 1) * 700, 800)) for i in range(3)]
    descriptions = [
        ("Skin", "Start at the surface", "A shared anatomical frame", panels[0]),
        ("Muscle", "Reveal the abdominal wall", "Layered teaching anatomy", panels[1]),
        ("Organs", "See what lies beneath", "Viscera in the same anatomical frame", panels[2]),
        ("Operative region", "Focus the rehearsal", "Right lower abdomen", panels[2].crop((35, 270, 415, 720))),
    ]
    outputs = []
    for index, (name, headline, detail, panel) in enumerate(descriptions, 1):
        im = Image.new("RGB", (1920, 1080), BG)
        d = ImageDraw.Draw(im)
        title(d, "SCALPAL / ANATOMY BRIEFING", name, "Skin → Muscle → Organs → Operative region")
        # One model at a time. Source-pixel crop only; no anatomical rearrangement.
        panel = panel.resize((int(panel.width * 842 / panel.height), 842), Image.Resampling.LANCZOS)
        x, y = 1000 + (820 - panel.width) // 2, 204 + (842 - panel.height) // 2
        im.paste(panel, (x, y))
        d.text((76, 348), f"0{index}", font=font(100, True), fill=PURPLE)
        d.text((76, 484), headline, font=font(43, True), fill=TEXT)
        d.text((76, 547), detail, font=font(29), fill=MUTED)
        for n, chip in enumerate(("SKIN", "MUSCLE", "ORGANS", "REGION")):
            xx = 76 + n * 202
            d.line((xx, 681, xx + 170, 681), fill=MINT if n == index - 1 else (52, 72, 76), width=5)
            d.text((xx, 702), chip, font=font(22, True), fill=MINT if n == index - 1 else MUTED)
        d.text((76, 955), "SYNTHETIC ATLAS DEMONSTRATION", font=font(24, True), fill=MINT)
        d.text((76, 995), "Generic teaching anatomy · not patient imaging", font=font(24), fill=MUTED)
        path = folder / f"demo-atlas-reveal-{index}-{name.lower().replace(' ', '-')}.png"
        im.save(path)
        outputs.append(path)
        print(path)
    (folder / "demo-atlas-reveal-timing.txt").write_text(
        "Skin 0.00–0.65 s\nMuscle 0.65–1.30 s\nOrgans 1.30–2.00 s\nOperative region 2.00–3.00 s\n"
        "Source: assets/anatomy/briefing-preview.png (synthetic atlas rendering).\n"
        "The region frame crops the patient-right lower abdomen; no new anatomy is inferred.\n")

    if video:
        # Native source-export edit: deterministic cuts over the already labelled stills.
        listing = folder / "demo-atlas-reveal.ffconcat"
        def quote(path):
            return str(path.resolve()).replace("'", "'\\''")
        listing.write_text("ffconcat version 1.0\n" + "".join(
            f"file '{quote(path)}'\nduration {duration}\n"
            for path, duration in zip(outputs, (.65, .65, .70, 1.0)))
            + f"file '{quote(outputs[-1])}'\n")
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-safe", "0",
                        "-i", str(listing), "-vf", "fps=30,tpad=stop_mode=clone:stop_duration=1", "-t", "3", "-c:v", "libx264",
                        "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-crf", "18",
                        str(folder / "demo-atlas-reveal.mp4")], check=True)
    (folder / "demo-visual-provenance.json").write_text(json.dumps({
        "atlas_source": "assets/anatomy/briefing-preview.png",
        "atlas_source_sha256": hashlib.sha256((ROOT / "assets/anatomy/briefing-preview.png").read_bytes()).hexdigest(),
        "atlas_evidence": "Synthetic atlas rendering; source-pixel crops, no anatomical repositioning",
        "surgery_evidence": "Synthetic tracked poses through real Unity Editor interaction fixture; no headset test",
        "surgery_camera": "Matched 1920x1080 overhead incision/closure views; wider delivered-organ stages 5–8",
        "closure_presentation": "Original seam vertices and widths; export-only depth visibility overlay",
        "runtime_change": "OpenWoundView material gloss only; no scored geometry or anchors changed",
    }, indent=2) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--video", action="store_true", help="Also encode the three-second atlas reveal with ffmpeg")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    label_surgery(args.output)
    atlas_reveal(args.output, args.video)


if __name__ == "__main__":
    main()
