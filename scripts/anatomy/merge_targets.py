#!/usr/bin/env python3
"""Merge authored exercise targets into the atlas without mutating upstream exports."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def main():
    path = ROOT / "apps/quest/Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json"
    atlas = json.loads(path.read_text())
    targets = json.loads((ROOT / "assets/anatomy/build/exercise-targets.json").read_text())
    system = targets["system"]
    system["inputHashes"] = targets.get("inputHashes", {})
    atlas["systems"] = [s for s in atlas["systems"] if s["id"] != system["id"]] + [system]
    atlas["parts"] = [p for p in atlas["parts"] if p["system"] != system["id"]] + targets["parts"]
    ids = [p["stableId"] for p in atlas["parts"]]
    assert len(ids) == len(set(ids)), "Supplement must not duplicate existing structure IDs"
    bundle = json.loads((ROOT / "apps/quest/Assets/Scalpal/Exercises/Resources/scalpal_bundle.json").read_text())
    mapped = {p["catalogId"] for p in atlas["parts"] if p["catalogId"]}
    atlas["unmappedCatalogIds"] = sorted(a["id"] for a in bundle["anatomy"] if a["id"] not in mapped)
    atlas["exerciseCoverage"] = []
    for procedure in bundle["procedures"]:
        required = set()
        for step in procedure["steps"]:
            check = step.get("check", {})
            if check.get("type") in ("touch_target", "identify_targets", "apply_count"):
                required.update(step.get("targets", []))
                required.update(check.get("targets", []))
            required.update(m["structure"] for m in step.get("mistakes", [])
                            if m.get("structure"))
        atlas["exerciseCoverage"].append({"procedureId": procedure["id"],
            "interactionTargets": sorted(required), "missingTargets": sorted(required - mapped)})
    path.write_text(json.dumps(atlas, indent=2) + "\n")
    print("Merged authored targets:", len(targets["parts"]))
    print("Unmapped catalog IDs:", atlas["unmappedCatalogIds"])


if __name__ == "__main__":
    main()
