# Open Surgery Visual Audit Captures

Original graphics-backed Unity `NativeSession` renders, October4,2026. Synthetic tracked tool poses drive production wall/contact/body-event consumers; these are not physical headset, participant or clinical images. See [the complete audit](../../docs/surgery-visual-audit.md) and [research criteria](../../docs/research/open-appendectomy-visuals.md).

`before-*` compares the prior scene (`0ff88d1` plus `fbb6067` closure correction); `after-*` shows the visual/persistence milestone committed in `5a6f502`. The later `049fd33` pool-display correction does not change these non-bleeding stage images. Close and learner views cover all ten stages. Selected full-size images include membrane tenting before the nick and the post-division specimen. The learner stands at patient right; this angle exposes the unresolved source-layout/rigid-group cecum occlusion.

Regenerate with `Scalpal.Surgery.Editor.SurgicalVisualAudit.Run`, Android target, a graphics device (omit `-nographics`), and `SCALPAL_STEP_RENDERS` / `SCALPAL_MARKING_RENDERS` set to existing absolute output directories. This fixture's completion marker does not assert clinical realism; inspect the images.
