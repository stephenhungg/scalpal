# Vision Detection Service

Owner: Matthew (Jarvis lane).

Local HTTP service that takes a **real camera image** plus text labels and returns named bounding boxes, so the Jarvis coach can say what is visible. Open-vocabulary detection with OWLv2 (Apache-2.0), running on Apple Silicon through PyTorch MPS, with a CPU fallback.

For the VR scene, use the scene graph instead. Unity already knows every organ and tool and can project their exact bounds; a detector would only add error. This service is for real camera frames, such as passthrough or webcam footage of physical props and hands.

## Status (October 3, 2026)

| Check | Result | Kind |
| --- | --- | --- |
| OWLv2 vs Grounding DINO on 6 Wikimedia photos, Apple M2 16 GB, MPS | OWLv2 chosen: cleaner scores, faster. See [model choice](#model-choice) | Measured, qualitative |
| Warm latency, OWLv2 at the default 768 input, 13 labels | 745 ms median per image (555 to 795 ms) | Measured on M2 under background load |
| Live HTTP round trip, 1024 px instrument tray photo, default labels | 518 to 638 ms per request, 48 detections | Measured on M2 |
| `uv run pytest` | 36 passed: validation, box normalization, label mapping, catalog id sync, CORS, one real OWLv2 inference | Local |
| Synthetic fine-tune smoke run (15 instruments, 90 Blender renders) | Held-out top-1 instrument class 1/15 zero-shot, 6/15 after 9 min. See [fine-tune](#fine-tune) | Synthetic renders only |
| Real Quest passthrough frames | **Not run.** No frames have been tested | None |
| Real physical surgical instruments | **Not run.** Kitchen tools and stock photos stood in | None |

## Run

Requires [uv](https://docs.astral.sh/uv/). Python 3.11 is pinned. The first run downloads the OWLv2 weights (about 620 MB) into the Hugging Face cache, `~/.cache/huggingface`. After that, it runs offline with `HF_HUB_OFFLINE=1`.

```sh
cd services/vision
uv sync
uv run scalpal-vision serve                      # http://127.0.0.1:8791, loads and warms the model first
uv run scalpal-vision serve --size 960           # native OWLv2 resolution: better on small objects, ~2x slower
uv run scalpal-vision detect photo.jpg --labels scissors hand "gloved hand"
uv run pytest
```

Startup takes 5 to 12 s (model load plus one warmup inference). `--device cpu` forces CPU, which measured about 10 s per image on the M2, too slow for live use. `--model grounding-dino` runs the comparison model.

## API

`POST /detect`

```json
{"image": "<base64 JPEG/PNG/WebP; a data: URL prefix is fine>",
 "labels": ["scissors", "forceps", "gloved hand", "lap_scissors"],
 "threshold": 0.2}
```

- `labels` is optional. It defaults to the instrument and hand set in `GET /models`. Labels can be free text, known prompts or catalog ids. A catalog id such as `lap_scissors` is sent to the detector as its canonical prompt (`scissors`). The limit is 32 labels of up to 64 characters each.
- `threshold` takes a value from 0 to 1 and defaults to 0.2. Use 0.3 or higher on cluttered scenes.
- The service downscales images so the long side is at most 1024 px. Encoded images over 8 MB are rejected.

Response, captured from the running service with the committed test image and `labels` omitted, first three detections:

```json
{"detections": [
   {"label": "scissors", "score": 0.9135,
    "box": {"x": 0.0, "y": 0.05664, "w": 0.9708, "h": 0.88965},
    "id": "lap_scissors", "kind": "instrument", "candidateIds": ["lap_scissors"]},
   {"label": "hand", "score": 0.3955,
    "box": {"x": 0.0, "y": 0.62305, "w": 0.97122, "h": 0.37695},
    "id": null, "kind": "generic", "candidateIds": []},
   {"label": "person", "score": 0.3374,
    "box": {"x": 0.0, "y": 0.62305, "w": 0.97122, "h": 0.37695},
    "id": null, "kind": "generic", "candidateIds": []}],
 "model": "owlv2", "ms": 458.0, "image": {"width": 278, "height": 480}}
```

- Boxes are normalized from 0 to 1 against the (downscaled) input image, with the origin at the top left. They are camera-image coordinates only. They carry no depth and no Unity, world or robot coordinates.
- Detections are sorted by score. Each label gets its own non-maximum suppression, so one object can appear under two labels (`hand` and `person`, or `scissors` and `knife`) if you ask for both.
- `id` is the catalog id when the prompt maps to exactly one. `candidateIds` lists every option when it does not: `trocar` maps to `trocar_5mm` and `trocar_12mm`, and `surgical stapler` maps to `endo_stapler` and `circular_stapler`. Generic labels such as `hand`, `glove` and `person` have `id: null`.
- Errors return `{"error": code}`. The codes are `invalid_request` (422, with `details`), `invalid_base64`, `invalid_image`, `unsupported_image_format`, `empty_image` and `image_too_large` (413).

`GET /health` returns `{status, model, device, warmupMs}`. `GET /models` returns the active model, the available backends and the default labels with their catalog mapping.

- The service listens on port 8791 on 127.0.0.1.
- CORS allows any `localhost`, `127.0.0.1` or `[::1]` origin.
- It never writes frames to disk and logs no request bodies.
- Inference is serialized, one frame at a time.

### Label mapping

`scalpal_vision/labels.py` maps detector prompts to the ids in `services/preop/src/catalog/instruments.ts` and `anatomy.ts`. A test checks that every mapped id still exists in those files. Some real-world stand-ins map deliberately to the closest catalog tool:

| Prompt(s) | Catalog id |
| --- | --- |
| scissors, surgical scissors, laparoscopic scissors | `lap_scissors` |
| scalpel, surgical blade, knife | `scalpel` |
| forceps, grasper, surgical/laparoscopic grasper | `atraumatic_grasper` |
| dissector, maryland dissector | `maryland_dissector` |
| trocar, surgical port | candidates `trocar_5mm`, `trocar_12mm` |
| laparoscope, endoscope | `laparoscope_30` |
| cautery hook, electrocautery | `hook_cautery` |
| clip applier, vessel sealer, suction tube/irrigator, specimen bag, fascial closure device, linear/circular stapler | their matching ids |
| liver, gallbladder, appendix, small bowel, colon, ... (opt-in, not defaults) | anatomy ids |
| hand, gloved hand, glove, person, surgical mask, operating table | none |

## Model choice

Both candidates ran through `scripts/bench.py` on the same machine and the same 13 labels: scissors, forceps, scalpel, knife, hand, gloved hand, person, whisk, spatula, ladle, strainer, operating table and surgical light. Threshold 0.2, images capped at 1024 px, median of 5 warm runs. The machine was an Apple M2 with 16 GB and MPS. Other jobs were running during the runs (1-minute load average 9 to 12), so absolute times are pessimistic. The relative order held across three runs.

| Model | License | Warm median | Load + first inference |
| --- | --- | --- | --- |
| OWLv2 base patch16 ensemble, input 768 (default) | Apache-2.0 | **745 ms** | 11.4 s + 0.7 s |
| OWLv2 base patch16 ensemble, native input 960 | Apache-2.0 | 1525 ms | 6.7 s + 1.0 s |
| Grounding DINO tiny | Apache-2.0 | 2278 ms | 5.5 s + 1.9 s |
| YOLO-World | AGPL-3.0 | not run | |

OWLv2 timings use fp16 on MPS and a PIL/torch preprocessing path. On the hand photo, fp16 changed the top scores by less than 0.001. The stock `Owlv2ImageProcessor` alone took 560 to 810 ms per image. The custom path takes 20 to 30 ms, with a mean absolute pixel difference of 0.006 in normalized units. Model forward on CPU took about 10 s.

Qualitative results at threshold 0.2. Scores are the top-scoring box per label. The annotated images are written to `data/bench/out/<model>/`.

| Image | OWLv2 (960) | Grounding DINO tiny |
| --- | --- | --- |
| Scissors on white | scissors 0.93; forceps 0.23 | scissors 0.57, plus false operating table 0.46, surgical light 0.30, whisk, knife, gloved hand, scalpel |
| Hand holding kitchen scissors | scissors 0.88 (full tool), person 0.45, hand 0.43, knife 0.44 on one blade | scissors 0.47, gloved hand 0.38 (the hand is bare), false scalpel 0.35, strainer 0.35, operating table 0.31 |
| Gloved hands on a shirt | gloved hand 0.40, hand 0.37, person 0.35; no false positives | person 0.53, gloved hand 0.53, hand 0.41, plus false scalpel 0.39, operating table 0.38 |
| Instrument tray (hemostats, forceps, retractor) | Boxes most clamp-style instruments individually, as "scissors" 0.53 to 0.66; at 768 many become "forceps" | Similar instrument boxes at about 0.5, but the top hit is operating table 0.67 |
| Kitchen utensil rack | knife, spatula, strainer, ladle and whisk found, with many overlapping low-score boxes | Same objects, plus a false operating table 0.45 and scissors 0.40 |
| Empty operating room | surgical lights and the table | surgical lights and the table, plus a false person 0.27 |

OWLv2 gives absent objects low scores, so a single threshold separates real detections from noise. Grounding DINO gave at least one absent label 0.3 or more on every image, which would make Jarvis talk about things that are not there. Neither model reliably separates scissors from forceps or hemostats. Ring-handled instruments look alike, and the label tends to follow whichever prompt is closest.

**YOLO-World** is likely to be much faster, at real-time rates. Its Ultralytics implementation is AGPL-3.0, and in a public repository with a networked service that obliges us to release the combined work under AGPL or buy an enterprise license. The gap from OWLv2 at 768 does not justify that for a 1 frame/s coach loop, so it was not evaluated.

## Fine-tune

The pipeline below is proven end to end on synthetic renders. It is not evidence of real-tool accuracy.

1. **Render.** `scripts/instruments/render_cv_dataset.py` renders each catalog instrument from random views with Blender. The `.blend` sources are in Git LFS, so run `git lfs pull --include="assets/instruments/*"` first. Call the Blender binary directly: the Homebrew `blender` symlink cannot find its bundled Python and crashes.

   ```sh
   /path/to/Blender.app/Contents/MacOS/Blender --background \
     --python scripts/instruments/render_cv_dataset.py -- \
     --root . --out "$PWD/services/vision/data/synthetic" --views 6
   ```

2. **Convert.** `uv run python scripts/prepare_finetune.py --dataset data/synthetic` reads the COCO visible-mask boxes and writes `train.jsonl`/`test.jsonl` and `prompts.json`. The last view of each instrument is held out. Prompts come from the catalog display names, one per id, for example "laparoscopic scissors" and "5 mm trocar".

3. **Train and score.** `uv run python scripts/finetune_owlv2.py --data data/synthetic/finetune --epochs 6 --save`
   - Only the OWLv2 class head trains. The vision tower, text tower and box head stay frozen.
   - A patch is a positive when its frozen predicted box has IoU of 0.5 or more with the ground truth. The loss is sigmoid focal.
   - It scores the held-out views before and after training and writes `runs/owlv2-synth/report.json`.
   - With `--save`, it writes `runs/owlv2-synth/weights`. Serve them with `scalpal-vision serve --weights runs/owlv2-synth/weights`.

**Measured smoke run.** The run used the default 768 input on the M2 with MPS:

- 90 renders at 384 px: 15 instruments with 6 random views each.
- Views 0 to 4 trained (75 images). View 5 was held out (15 images).
- 6 epochs, learning rate 1e-4, 395 k trainable parameters, 538 s of training.
- Mean loss fell from 1.38 in the first epoch to 0.33 in the last.

All 15 catalog prompts compete on every image:

| Split | top-1 class | top-1 class + IoU >= 0.5 | Mean IoU of best box for the true prompt |
| --- | --- | --- | --- |
| Held-out views, zero-shot | 0.067 (1/15) | 0.067 | 0.683 |
| Held-out views, fine-tuned | **0.40 (6/15)** | **0.40** | 0.740 |
| Training views, fine-tuned | 0.52 | 0.51 | 0.716 |

The pipeline works and moves the numbers in the right direction:

- Zero-shot OWLv2 boxes the rendered tools well but cannot name them among 15 similar laparoscopic instruments.
- A few minutes of class-head training lifts held-out top-1 from 1/15 to 6/15.
- The test set has 15 images, so one image is about 7 points.
- The 5 mm and 12 mm trocars render almost identically and cannot be separated.

None of this says anything about real photos. A first variant also trained the box head with one-to-one Hungarian matching, and it hurt localization: held-out mean IoU dropped from 0.68 to 0.26. The script no longer does that.

Datasets, renders, run outputs and weights are gitignored (`data/`, `runs/`, `models/`). Real-tool fine-tuning still needs photos of the physical props or tools, with labels and a held-out split. Nobody has checked whether the fine-tuned class head still detects hands and other generic labels. Until someone measures that, serve fine-tuned weights only for instrument prompts.

## Limits

- **Open-vocabulary detectors are unreliable on surgical anatomy.** They were trained on web photos, not intraoperative images. Expect missed or invented organ boxes. In the VR scene, organ and tool boxes must come from the scene graph, which is exact. Anatomy labels are opt-in here and never in the defaults.
- This service is for real camera images only. Raw passthrough cannot see virtual geometry, so it will never detect VR tools or organs.
- Fine-grained instrument identity is weak. Scissors, forceps, hemostats and graspers get confused, and two trocar sizes are indistinguishable. Use `candidateIds` and treat the label as a hint.
- Boxes are 2D image coordinates with no depth. Mapping them into Unity or robot space needs calibrated camera intrinsics and extrinsics plus depth, none of which this service provides.
- Latency is about 0.75 s per frame on a loaded M2 at the default 768 input. That supports roughly 1 frame/s, not real-time tracking. Inference is serialized, so concurrent callers queue.
- Scores are uncalibrated similarities, not probabilities. Pick thresholds per label on real footage.
- Measured only on 6 stock photos and synthetic renders. No Quest passthrough frame, real surgical instrument or gloved surgical scene has been evaluated.

## Licenses

- **Code:** this repository's license.
- **OWLv2** (`google/owlv2-base-patch16-ensemble`): Apache-2.0.
- **Grounding DINO tiny** (`IDEA-Research/grounding-dino-tiny`): Apache-2.0.
- **Hugging Face transformers:** Apache-2.0.
- **PyTorch:** BSD-3-Clause.
- **FastAPI:** MIT.
- **Model weights** are downloaded at run time and never committed.
- **YOLO-World / Ultralytics:** AGPL-3.0. Not used.

The test fixture `tests/fixtures/hand_holding_scissors.jpg` is public domain; see [its attribution](tests/fixtures/ATTRIBUTION.md).

The benchmark images are fetched by `scripts/fetch_bench_images.py` and are not committed:

| Wikimedia Commons file | License | Author |
| --- | --- | --- |
| [Gfp-hand-holding-scissors.jpg](https://commons.wikimedia.org/wiki/File:Gfp-hand-holding-scissors.jpg) | Public Domain | Yinan Chen |
| [Kitchen utensils-01.jpg](https://commons.wikimedia.org/wiki/File:Kitchen_utensils-01.jpg) | CC BY-SA 2.0 | Jeppestown |
| [Laparoscopic operating theatre.jpg](https://commons.wikimedia.org/wiki/File:Laparoscopic_operating_theatre.jpg) | CC BY-SA 3.0 | Dr.jayesh amin |
| [Pair of scissors on white background.jpg](https://commons.wikimedia.org/wiki/File:Pair_of_scissors_on_white_background.jpg) | CC BY-SA 4.0 | Help e choose |
| [Person wearing blue gloves preparing for a task in a kitchen setting.jpg](https://commons.wikimedia.org/wiki/File:Person_wearing_blue_gloves_preparing_for_a_task_in_a_kitchen_setting.jpg) | CC BY 2.0 | Shixart1985 |
| [Surgical Instruments 01.jpg](https://commons.wikimedia.org/wiki/File:Surgical_Instruments_01.jpg) | CC0 | Armin (Wikimedia user) |

Frames sent to this service may show participants. Do not save them, and do not use them for training without the rights review in `docs/research/README.md`.
