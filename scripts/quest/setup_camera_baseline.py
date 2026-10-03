#!/usr/bin/env python3
"""Reconstruct the pinned camera experiment without committing its caches or footage."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess


def main():
    repo = Path(__file__).resolve().parents[2]
    bundle = repo / "experiments/quest-camera-baseline"
    source = json.loads((bundle / "SOURCE.json").read_text())
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", type=Path, default=bundle / ".checkout")
    args = parser.parse_args()
    destination = args.destination.expanduser().resolve()
    if destination.exists():
        parser.error("destination already exists; choose a new directory to preserve existing work")
    if not shutil.which("git"):
        parser.error("git is required")
    subprocess.run(["git", "lfs", "version"], check=True)
    destination.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["git", "clone", "--no-checkout", source["repository"], str(destination)], check=True)
    subprocess.run(["git", "checkout", "--detach", source["revision"]], cwd=destination, check=True)
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=destination, text=True).strip()
    if revision != source["revision"]:
        raise RuntimeError("upstream checkout did not match the pinned revision")
    subprocess.run(["git", "lfs", "pull"], cwd=destination, check=True)
    patch = str(bundle / source["localChanges"])
    subprocess.run(["git", "apply", "--check", patch], cwd=destination, check=True)
    subprocess.run(["git", "apply", patch], cwd=destination, check=True)
    overlay = bundle / source["additionalAssets"]
    for path in overlay.rglob("*"):
        if path.is_file():
            target = destination / path.relative_to(overlay)
            target.parent.mkdir(parents=True, exist_ok=True)
            if target.exists():
                raise RuntimeError(f"overlay would replace an upstream file: {target}")
            shutil.copyfile(path, target)
    print(f"Camera baseline ready: {destination}")
    print("Open with Unity " + source["unityVersion"] + ". No build, installation or capture was started.")


if __name__ == "__main__":
    main()
