"""Download the freely licensed Wikimedia Commons images used for the detector comparison.

    uv run python scripts/fetch_eval_images.py            # -> data/bench/*.jpg + data/bench/manifest.json

Images stay in the gitignored data/ folder. The titles, pages and licenses are listed in
README.md so the comparison can be reproduced without redistributing the files.
"""

from __future__ import annotations

import json
import re
import urllib.parse
import urllib.request
from pathlib import Path

TITLES = [
    "File:Pair of scissors on white background.jpg",
    "File:Gfp-hand-holding-scissors.jpg",
    "File:Surgical Instruments 01.jpg",
    "File:Kitchen utensils-01.jpg",
    "File:Laparoscopic operating theatre.jpg",
    "File:Person wearing blue gloves preparing for a task in a kitchen setting.jpg",
]
HEADERS = {"User-Agent": "scalpal-vision/0.1 (https://github.com/stephenhungg/scalpal)"}
OUT = Path(__file__).resolve().parent.parent / "data" / "bench"


def slug(title: str) -> str:
    stem = title.removeprefix("File:").rsplit(".", 1)[0].lower()
    return re.sub(r"[^a-z0-9]+", "_", stem).strip("_") + ".jpg"


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    query = urllib.parse.urlencode(
        {
            "action": "query",
            "format": "json",
            "titles": "|".join(TITLES),
            "prop": "imageinfo",
            "iiprop": "url|extmetadata",
            "iiurlwidth": "1280",
        }
    )
    request = urllib.request.Request("https://commons.wikimedia.org/w/api.php?" + query, headers=HEADERS)
    pages = json.load(urllib.request.urlopen(request))["query"]["pages"].values()
    manifest = []
    for page in pages:
        info = page["imageinfo"][0]
        meta = info["extmetadata"]
        name = slug(page["title"])
        data = urllib.request.urlopen(urllib.request.Request(info["thumburl"], headers=HEADERS)).read()
        (OUT / name).write_bytes(data)
        manifest.append(
            {
                "file": name,
                "title": page["title"],
                "page": info["descriptionurl"],
                "license": meta["LicenseShortName"]["value"],
                "artist": re.sub("<[^>]+>", "", meta.get("Artist", {}).get("value", "")).strip(),
            }
        )
    (OUT / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
