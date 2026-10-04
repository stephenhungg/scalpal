import re
from pathlib import Path

import pytest

from scalpal_vision.detector import RawDetection, _match_phrase, iou, nms, normalize_box
from scalpal_vision.labels import BY_PROMPT, DEFAULT_LABELS, INSTRUMENT_IDS, resolve

REPO = Path(__file__).resolve().parents[3]


def test_normalize_box_top_left_origin():
    assert normalize_box((100, 50, 300, 250), 400, 500) == {"x": 0.25, "y": 0.1, "w": 0.5, "h": 0.4}


def test_normalize_box_clamps_and_orders():
    # OWLv2 boxes can extend into its square padding or past the image edge.
    assert normalize_box((-20, 380, 450, -10), 400, 400) == {"x": 0.0, "y": 0.0, "w": 1.0, "h": 0.95}
    assert normalize_box((500, 500, 600, 600), 400, 400)["w"] == 0.0


def test_iou_and_nms():
    a, b, c = (0, 0, 10, 10), (1, 1, 11, 11), (50, 50, 60, 60)
    assert iou(a, a) == 1.0 and iou(a, c) == 0.0
    kept = nms([RawDetection(0, 0.5, b), RawDetection(0, 0.9, a), RawDetection(1, 0.4, b), RawDetection(0, 0.3, c)])
    assert [(d.label_index, d.score) for d in kept] == [(0, 0.9), (1, 0.4), (0, 0.3)]


@pytest.mark.parametrize(
    "label,prompt,id",
    [
        ("scissors", "scissors", "lap_scissors"),
        ("Surgical  Scissors", "surgical scissors", "lap_scissors"),
        ("lap_scissors", "scissors", "lap_scissors"),
        ("knife", "knife", "scalpel"),
        ("scalpel", "scalpel", "scalpel"),
        ("forceps", "forceps", "atraumatic_grasper"),
        ("clip_applier", "clip applier", "clip_applier"),
        ("trocar_12mm", "trocar", "trocar_12mm"),
        ("trocar", "trocar", None),
        ("hand", "hand", None),
        ("small_bowel", "small bowel", "small_bowel"),
        ("purple teapot", "purple teapot", None),
    ],
)
def test_resolve(label, prompt, id):
    got_prompt, info = resolve(label)
    assert (got_prompt, info.id) == (prompt, id)


def test_ambiguous_prompt_lists_candidates():
    assert resolve("trocar")[1].as_dict() == {"id": None, "kind": "instrument", "candidateIds": ["trocar_5mm", "trocar_12mm"]}
    assert resolve("hand")[1].as_dict() == {"id": None, "kind": "generic", "candidateIds": []}


def test_every_catalog_instrument_has_a_prompt():
    covered = {i for entry in BY_PROMPT.values() for i in (entry.candidate_ids or (entry.id,)) if i}
    assert set(INSTRUMENT_IDS) <= covered
    assert all(label in BY_PROMPT for label in DEFAULT_LABELS)


def test_ids_match_preop_catalog():
    catalog = REPO / "services/preop/src/catalog"
    if not catalog.exists():
        pytest.skip("preop catalog not present")
    pattern = re.compile(r'(?:instrument|structure)\("([a-z0-9_]+)"')
    known = set(pattern.findall((catalog / "instruments.ts").read_text()))
    known |= set(pattern.findall((catalog / "anatomy.ts").read_text()))
    assert set(INSTRUMENT_IDS) == set(pattern.findall((catalog / "instruments.ts").read_text()))
    ids = {i for entry in BY_PROMPT.values() for i in (entry.candidate_ids or (entry.id,)) if i}
    assert ids <= known, ids - known


def test_grounding_dino_phrase_matching():
    labels = ["scissors", "gloved hand", "hand"]
    lookup = {label: i for i, label in enumerate(labels)}
    assert _match_phrase("hand", labels, lookup) == 2
    assert _match_phrase("gloved hand", labels, lookup) == 1
    assert _match_phrase("gloved", labels, lookup) == 1
    assert _match_phrase("teapot", labels, lookup) is None
