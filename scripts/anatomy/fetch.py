#!/usr/bin/env python3
"""Verify tracked full-resolution atlas sources or recover them from pinned upstream URLs."""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


def digest(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()


def fetch(item, cache):
    path = cache / item["filename"]
    data = path.read_bytes() if path.exists() else urllib.request.urlopen(item["url"], timeout=120).read()
    if item.get("bytes") and len(data) != item["bytes"]:
        raise ValueError(f"Size mismatch: {path.name}")
    if item.get("gitBlob") and digest(data) != item["gitBlob"]:
        raise ValueError(f"Git blob mismatch: {path.name}")
    if item.get("sha256") and hashlib.sha256(data).hexdigest() != item["sha256"]:
        raise ValueError(f"SHA256 mismatch: {path.name}")
    partial = path.with_suffix(path.suffix + ".partial")
    partial.write_bytes(data)
    partial.replace(path)
    print(f"verified {path.name}: {len(data):,} bytes", flush=True)
    return {**item, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--lock", action="store_true", help="Record verified SHA256 hashes in sources.json")
    args = p.parse_args()
    manifest = ROOT / "assets/anatomy/sources.json"
    doc = json.loads(manifest.read_text())
    cache = ROOT / "assets/anatomy/originals"
    cache.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        results = list(pool.map(lambda item: fetch(item, cache), doc["sources"]))
    if args.lock:
        doc["sources"] = results
        manifest.write_text(json.dumps(doc, indent=2) + "\n")


if __name__ == "__main__":
    main()
