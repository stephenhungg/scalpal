"""Detector prompts mapped to Scalpal catalog ids.

Instrument ids mirror services/preop/src/catalog/instruments.ts and anatomy ids mirror
services/preop/src/catalog/anatomy.ts. Keep them in sync by hand; this service does not
import the TypeScript catalog.

A prompt resolves to exactly one catalog id when the mapping is unambiguous. When a real
object could be several catalog tools (a trocar of unknown size), `id` is None and
`candidateIds` lists the options. Generic labels such as "hand" have no catalog id.
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class LabelInfo:
    prompt: str
    id: str | None = None
    kind: str = "generic"  # instrument | anatomy | generic
    candidate_ids: tuple[str, ...] = field(default_factory=tuple)

    def as_dict(self) -> dict:
        ids = list(self.candidate_ids) or ([self.id] if self.id else [])
        return {"id": self.id, "kind": self.kind, "candidateIds": ids}


def _instrument(prompt: str, id: str | None, *candidates: str) -> LabelInfo:
    return LabelInfo(prompt, id, "instrument", tuple(candidates))


def _anatomy(prompt: str, id: str) -> LabelInfo:
    return LabelInfo(prompt, id, "anatomy")


def _generic(prompt: str) -> LabelInfo:
    return LabelInfo(prompt)


INSTRUMENT_IDS = (
    "scalpel",
    "trocar_5mm",
    "trocar_12mm",
    "laparoscope_30",
    "atraumatic_grasper",
    "maryland_dissector",
    "hook_cautery",
    "vessel_sealer",
    "clip_applier",
    "lap_scissors",
    "endo_stapler",
    "circular_stapler",
    "suction_irrigator",
    "retrieval_bag",
    "fascial_closure",
)

_ENTRIES: tuple[LabelInfo, ...] = (
    # Instruments. Real-world stand-ins (kitchen scissors, a knife) map to the closest catalog tool.
    _instrument("scalpel", "scalpel"),
    _instrument("surgical blade", "scalpel"),
    _instrument("knife", "scalpel"),
    _instrument("scissors", "lap_scissors"),
    _instrument("surgical scissors", "lap_scissors"),
    _instrument("laparoscopic scissors", "lap_scissors"),
    _instrument("forceps", "atraumatic_grasper"),
    _instrument("grasper", "atraumatic_grasper"),
    _instrument("surgical grasper", "atraumatic_grasper"),
    _instrument("laparoscopic grasper", "atraumatic_grasper"),
    _instrument("dissector", "maryland_dissector"),
    _instrument("maryland dissector", "maryland_dissector"),
    _instrument("trocar", None, "trocar_5mm", "trocar_12mm"),
    _instrument("surgical port", None, "trocar_5mm", "trocar_12mm"),
    _instrument("laparoscope", "laparoscope_30"),
    _instrument("endoscope", "laparoscope_30"),
    _instrument("cautery hook", "hook_cautery"),
    _instrument("electrocautery", "hook_cautery"),
    _instrument("vessel sealer", "vessel_sealer"),
    _instrument("clip applier", "clip_applier"),
    _instrument("surgical stapler", None, "endo_stapler", "circular_stapler"),
    _instrument("linear stapler", "endo_stapler"),
    _instrument("circular stapler", "circular_stapler"),
    _instrument("suction tube", "suction_irrigator"),
    _instrument("suction irrigator", "suction_irrigator"),
    _instrument("specimen bag", "retrieval_bag"),
    _instrument("fascial closure device", "fascial_closure"),
    # Anatomy. Opt-in only: open-vocabulary detectors are unreliable on surgical anatomy.
    _anatomy("liver", "liver"),
    _anatomy("gallbladder", "gallbladder"),
    _anatomy("stomach", "stomach"),
    _anatomy("small bowel", "small_bowel"),
    _anatomy("small intestine", "small_bowel"),
    _anatomy("appendix", "appendix"),
    _anatomy("cecum", "cecum"),
    _anatomy("colon", "transverse_colon"),
    _anatomy("omentum", "greater_omentum"),
    _anatomy("bladder", "urinary_bladder"),
    _anatomy("kidney", "right_kidney"),
    _anatomy("heart", "heart"),
    _anatomy("lungs", "lungs"),
    _anatomy("abdominal wall", "abdominal_wall"),
    _anatomy("belly button", "umbilicus"),
    # Generic scene labels with no catalog id.
    _generic("hand"),
    _generic("gloved hand"),
    _generic("glove"),
    _generic("person"),
    _generic("surgical mask"),
    _generic("operating table"),
)

BY_PROMPT: dict[str, LabelInfo] = {entry.prompt: entry for entry in _ENTRIES}

# The first prompt listed for each instrument id is its canonical prompt, so callers can
# send a catalog id ("lap_scissors") as a label and get a sensible detector prompt.
CANONICAL_PROMPT: dict[str, str] = {}
for _entry in _ENTRIES:
    for _id in _entry.candidate_ids or ((_entry.id,) if _entry.id else ()):
        CANONICAL_PROMPT.setdefault(_id, _entry.prompt)

DEFAULT_LABELS: tuple[str, ...] = (
    "scissors",
    "scalpel",
    "knife",
    "forceps",
    "grasper",
    "trocar",
    "laparoscope",
    "clip applier",
    "surgical stapler",
    "suction tube",
    "hand",
    "gloved hand",
    "person",
)


def normalize_prompt(label: str) -> str:
    return " ".join(label.strip().lower().replace("_", " ").split())


def resolve(label: str) -> tuple[str, LabelInfo]:
    """Return (detector prompt, catalog info) for a caller label.

    Accepts free text ("bent forceps"), a known prompt ("scissors") or a catalog id
    ("lap_scissors"). Unknown text is passed to the detector unchanged with no id.
    """
    raw = label.strip()
    if raw in CANONICAL_PROMPT and raw not in BY_PROMPT:
        prompt = CANONICAL_PROMPT[raw]
        # A catalog id request is unambiguous even if its prompt has several candidates.
        return prompt, LabelInfo(prompt, raw, BY_PROMPT[prompt].kind, (raw,))
    prompt = normalize_prompt(raw)
    return prompt, BY_PROMPT.get(prompt, LabelInfo(prompt))
