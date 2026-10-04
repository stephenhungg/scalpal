# Open Surgery Visual Audit Captures

Original graphics-backed Unity `NativeSession` renders, October4,2026. Synthetic tracked tool poses drive production wall/contact/body-event consumers; these are not physical headset, participant or clinical images. See [the complete audit](../../docs/surgery-visual-audit.md) and [research criteria](../../docs/research/open-appendectomy-visuals.md).

`before-*` compares the prior scene (`0ff88d1` plus `fbb6067` closure correction); `after-*` shows the visual/persistence milestone committed in `5a6f502`. The later `049fd33` pool-display correction does not change these non-bleeding stage images. Close and learner views cover all ten stages. Selected full-size images include membrane tenting before the nick and the post-division specimen. The learner stands at patient right; this angle exposes the unresolved source-layout/rigid-group cecum occlusion.

Regenerate with `Scalpal.Surgery.Editor.SurgicalVisualAudit.Run`, Android target, a graphics device (omit `-nographics`), and `SCALPAL_STEP_RENDERS` / `SCALPAL_MARKING_RENDERS` set to existing absolute output directories. This fixture's completion marker does not assert clinical realism; inspect the images.

## Wrist-exposure follow-up

Added `exposure-vr-before.png`, `exposure-vr-after.png`, and `exposure-ar-after.png`: graphics-backed Unity6000.0.66f2 actual NativeSession/source geometry, captured from the47,713-check `OrganExposureValidation` on583c679 plus the wrist/lighting/patient-shading update. Both close VR images use the same fixed patient-right camera; the only operative difference is the settled Babcock wrist pose. The AR image uses a synthetic translated/rotated registration and compositor-ready flag; black background is not physical passthrough. Prior wall steps, XR samples and timing are synthetic; these are not headset or clinical evidence. Regenerate using `SCALPAL_ORGAN_EXPOSURE_RENDERS` with the validation entry point.

- `exposure-vr-before.png` SHA256 `0e72904a6092d05fab3a17a874364c4cf5343a0c9be5aa9819192268eaffcb3c`.
- `exposure-vr-after.png` SHA256 `7905a9db3a4428e7a91a19688ea054b3e94abc40990134252695516e1a773b42`.
- `exposure-ar-after.png` SHA256 `7683e292aff042346ea3ba8fe30ae0c4a0b79550e57516d554ad2584ac439d66`.

## Code19 ten-stage lighting review

`lighting-stage-00-mark.png` through `lighting-stage-09-closed.png` are selected actual NativeSession graphics captures on packaged0cd8b1d, regenerated with `SCALPAL_STEP_RENDERS` and `OpenStepVisualsValidation.Run` (147 passed). All10 selected images were visually inspected. They show the same fixed close view for entry/closure and organ view for delivery/division/inspection. Tools are hidden by the validation; controller/body/previous-action inputs are synthetic. The consumer fixture does not turn the wrist in its delivery step, so its base remains partly obscured; the separate exposure captures demonstrate that operation. Tissue remains stylized, bands are strongly separated, and the body silhouette is low resolution. No headset or clinician realism approval is claimed. An initial export was stopped because its output flag was missing; the next lacked its output folder. Both unsuccessful runs are excluded from the passing147 evidence.

- `lighting-stage-00-mark.png` SHA256 `94e7b30d31bd320005def53e42ebf1237afbed766ca14564b735043dbcfa7b8b`.
- `lighting-stage-01-skin.png` SHA256 `d244a28805f2b62926d4d8c8ad54d71acaa27f000ba2b273e6210cceab4d7884`.
- `lighting-stage-02-fascia.png` SHA256 `c0c8e864fe8c976d542bca664b7898363dd5f3016f91ca1cd551b66299de76c8`.
- `lighting-stage-03-muscle.png` SHA256 `f39f288a94a01b809c2124edb64ebbeaa0df1f4f3101ad478e2117f1694ae6a2`.
- `lighting-stage-04-peritoneum.png` SHA256 `bde01684aa8428879d54163ca1eb31997eac1c3c2d6dcef09b521f63bcf74619`.
- `lighting-stage-05-delivered.png` SHA256 `7346554eb5bffdbca69cc72103aaf386a55309a4e2ba2563a03187e0c5ea2f2a`.
- `lighting-stage-06-mesoappendix.png` SHA256 `0999ed7d72bd765a0b6a2c58bc199ef2b5481c85c368654051922a2d3a820343`.
- `lighting-stage-07-base.png` SHA256 `ac326d3034abfa6f8deeae475452f1880cc370d0e585a6784c0398ce8f804635`.
- `lighting-stage-08-clean.png` SHA256 `fa8d216d0731c13858e86b139d962bd2d6dda9617e74d0c9b07c72bea8b67b26`.
- `lighting-stage-09-closed.png` SHA256 `a1c4cecf280786a47a346853528a4857a731839c0158ab1beb551815b7725acf`.
