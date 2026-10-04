"""Convert the synthetic COCO dataset into OWLv2 fine-tune records.

1. Render the dataset (Blender 4.5; call the app binary directly, a symlinked `blender`
   cannot find its bundled Python):

       /path/to/Blender.app/Contents/MacOS/Blender --background \
         --python scripts/instruments/render_cv_dataset.py -- \
         --root . --out "$PWD/services/vision/data/synthetic" --views 6

2. Convert it (from services/vision):

       uv run python scripts/prepare_finetune.py --dataset data/synthetic --holdout-views 1

Output: <dataset>/finetune/{train,test}.jsonl and prompts.json. Each record is

    {"image": "<abs path>", "width": W, "height": H,
     "boxes": [{"id": "lap_scissors", "class": 9, "xyxy": [x0, y0, x1, y1]}]}

where xyxy is in pixels (top-left origin), copied from the COCO visible-mask box. The
split is by view index so every instrument appears in both splits, and no rendered view
is in both. The training script converts boxes to OWLv2's padded-square normalized space.
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

# One text prompt per catalog instrument, from services/preop/src/catalog/instruments.ts
# display names. Fine-tuning learns to align these prompts with our rendered tools.
FINETUNE_PROMPTS = {
    "scalpel": "scalpel",
    "trocar_5mm": "5 mm trocar",
    "trocar_12mm": "12 mm trocar",
    "laparoscope_30": "laparoscope",
    "atraumatic_grasper": "laparoscopic grasper",
    "maryland_dissector": "maryland dissector",
    "hook_cautery": "monopolar cautery hook",
    "vessel_sealer": "bipolar vessel sealer",
    "clip_applier": "clip applier",
    "lap_scissors": "laparoscopic scissors",
    "endo_stapler": "endoscopic linear stapler",
    "circular_stapler": "circular stapler",
    "suction_irrigator": "suction irrigator",
    "retrieval_bag": "specimen retrieval bag",
    "fascial_closure": "fascial closure device",
}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dataset", type=Path, required=True, help="folder with annotations.json")
    parser.add_argument("--holdout-views", type=int, default=1, help="last N views per tool go to test")
    args = parser.parse_args()
    root = args.dataset.resolve()
    coco = json.loads((root / "annotations.json").read_text())
    categories = {c["id"]: c["name"] for c in coco["categories"]}
    ids = list(FINETUNE_PROMPTS)
    missing = set(categories.values()) - set(ids)
    if missing:
        raise SystemExit(f"no fine-tune prompt for {sorted(missing)}")
    boxes = defaultdict(list)
    for ann in coco["annotations"]:
        x, y, w, h = ann["bbox"]
        name = categories[ann["category_id"]]
        boxes[ann["image_id"]].append({"id": name, "class": ids.index(name), "xyxy": [x, y, x + w, y + h]})
    views = defaultdict(list)
    for image in coco["images"]:
        views[image["instrumentId"]].append(image)
    split = {"train": [], "test": []}
    for tool, images in views.items():
        images.sort(key=lambda im: im["file_name"])
        for index, image in enumerate(images):
            part = "test" if index >= len(images) - args.holdout_views else "train"
            split[part].append(
                {
                    "image": str(root / image["file_name"]),
                    "width": image["width"],
                    "height": image["height"],
                    "boxes": boxes[image["id"]],
                }
            )
    out = root / "finetune"
    out.mkdir(exist_ok=True)
    for part, records in split.items():
        (out / f"{part}.jsonl").write_text("".join(json.dumps(r) + "\n" for r in records))
    (out / "prompts.json").write_text(json.dumps({"ids": ids, "prompts": [FINETUNE_PROMPTS[i] for i in ids]}, indent=2) + "\n")
    print(f"train={len(split['train'])} test={len(split['test'])} classes={len(ids)} -> {out}")


if __name__ == "__main__":
    main()
