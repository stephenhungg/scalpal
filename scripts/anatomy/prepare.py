#!/usr/bin/env python3
"""Download, build, and verify the atlas with a working Blender installation."""
import argparse
from pathlib import Path
import subprocess
import shutil
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--blender", default="blender", help="Blender executable, including full app-bundle path on macOS")
    parser.add_argument("--skip-build", action="store_true", help="Verify existing exports and fill missing metadata")
    args = parser.parse_args()
    # Blender locates its bundled Python resources relative to its executable.
    # Resolve shell symlinks rather than invoking a detached binary alias.
    found = shutil.which(args.blender)
    if not found:
        parser.error("Blender executable not found: " + args.blender)
    blender = str(Path(found).resolve())
    if not args.skip_build:
        subprocess.run([sys.executable, "scripts/anatomy/fetch.py"], cwd=ROOT, check=True)
        subprocess.run([blender, "--background", "--factory-startup", "--python-exit-code", "1",
                        "--python", "scripts/anatomy/build.py"], cwd=ROOT, check=True)
    subprocess.run([blender, "--background", "--factory-startup", "--python-exit-code", "1",
                    "--python", "scripts/anatomy/verify.py"], cwd=ROOT, check=True)
    atlas = ROOT / "apps/quest/Assets/Scalpal/Anatomy"
    for path in [atlas, *sorted(atlas.rglob("*"))]:
        if path.suffix == ".meta":
            continue
        meta = Path(str(path) + ".meta")
        if meta.exists():
            continue
        guid = uuid.uuid5(uuid.NAMESPACE_URL, "scalpal/" + path.relative_to(ROOT).as_posix()).hex
        text = f"fileFormatVersion: 2\nguid: {guid}\n"
        if path.is_dir():
            text += "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n"
        meta.write_text(text)
    print("Atlas verified; Unity asset GUIDs preserved/generated.")


if __name__ == "__main__":
    main()
