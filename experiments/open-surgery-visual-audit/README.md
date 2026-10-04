# Open Surgery Visual Audit Captures

Original graphics-backed Unity `NativeSession` renders, October4,2026. Synthetic tracked tool poses drive production wall/contact/body-event consumers; these are not physical headset, participant or clinical images. See [the complete audit](../../docs/surgery-visual-audit.md) and [research criteria](../../docs/research/open-appendectomy-visuals.md).

`before-*` compares the prior scene (`0ff88d1` plus `fbb6067` closure correction); `after-*` shows the visual/persistence milestone committed in `5a6f502`. The later `049fd33` pool-display correction does not change these non-bleeding stage images. Close and learner views cover all ten stages. Selected full-size images include membrane tenting before the nick and the post-division specimen. The learner stands at patient right; this angle exposes the unresolved source-layout/rigid-group cecum occlusion.

Regenerate with `Scalpal.Surgery.Editor.SurgicalVisualAudit.Run`, Android target, a graphics device (omit `-nographics`), and `SCALPAL_STEP_RENDERS` / `SCALPAL_MARKING_RENDERS` set to existing absolute output directories. This fixture's completion marker does not assert clinical realism; inspect the images.

## Wrist-exposure follow-up

Added `exposure-vr-before.png`, `exposure-vr-after.png`, and `exposure-ar-after.png`: graphics-backed Unity6000.0.66f2 actual NativeSession/source geometry, captured from the47,713-check `OrganExposureValidation` on583c679 plus the wrist/lighting/patient-shading update. Both close VR images use the same fixed patient-right camera; the only operative difference is the settled Babcock wrist pose. The AR image uses a synthetic translated/rotated registration and compositor-ready flag; black background is not physical passthrough. Prior wall steps, XR samples and timing are synthetic; these are not headset or clinical evidence. Regenerate using `SCALPAL_ORGAN_EXPOSURE_RENDERS` with the validation entry point.

- `exposure-vr-before.png` SHA256 `0e72904a6092d05fab3a17a874364c4cf5343a0c9be5aa9819192268eaffcb3c`.
- `exposure-vr-after.png` SHA256 `7905a9db3a4428e7a91a19688ea054b3e94abc40990134252695516e1a773b42`.
- `exposure-ar-after.png` SHA256 `7683e292aff042346ea3ba8fe30ae0c4a0b79550e57516d554ad2584ac439d66`.
