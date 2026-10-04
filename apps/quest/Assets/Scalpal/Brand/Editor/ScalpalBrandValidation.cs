using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Scalpal.Shell;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Brand.Editor
{
    /// <summary>
    /// Brand contract checks: static SDF fonts that cover every UI string, render ordering, the
    /// "Scalpal" name, level panel placement, and the aim-pose pointer geometry (origin, direction,
    /// end point, reticle, accent, hiding). Editor evidence only; not a headset pass.
    /// </summary>
    public static class ScalpalBrandValidation
    {
        static int checks;
        static readonly string[] UiSources = { "Assets/Scalpal/Shell/Runtime", "Assets/Scalpal/EncounterOffice/Runtime", "Assets/Scalpal/Handoff/Runtime", "Assets/Scalpal/Recap/Runtime", "Assets/Scalpal/Quest/Runtime" };

        [MenuItem("Scalpal/Brand/Validate Brand and Pointer")]
        public static int Run()
        {
            checks = 0;
            ValidateAssets();
            ValidateStrings();
            ValidatePlacement();
            ValidatePointer();
            Debug.Log("SCALPAL_BRAND_VERIFY_OK checks=" + checks + " staticSdf=true aimPose=true headset=false");
            return checks;
        }

        static void ValidateAssets()
        {
            var brand = AssetDatabase.LoadAssetAtPath<ScalpalBrand>(ScalpalBrandBuild.BrandPath);
            Check(brand && Resources.Load<ScalpalBrand>(ScalpalBrand.ResourcePath) == brand, "brand style ships from Resources/ScalpalBrand");
            Check(brand.display && brand.display.faceInfo.familyName == "Instrument Serif", "display face is Instrument Serif");
            Check(brand.body && brand.body.faceInfo.familyName == "Geist Mono" && brand.label && brand.label.faceInfo.familyName == "Geist Mono", "body and label faces are Geist Mono");
            foreach (var font in new[] { brand.display, brand.body, brand.label })
            {
                Check(font.atlasPopulationMode == AtlasPopulationMode.Static && !font.sourceFontFile, "font atlas is static (nothing rasterised on Quest): " + font.name);
                Check(font.atlasTexture && font.atlasTexture.width >= 1024 && AssetDatabase.IsSubAsset(font.atlasTexture), "font atlas is baked into the asset: " + font.name);
                Check(font.atlasRenderMode == UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, "font atlas is a signed distance field: " + font.name);
            }
            Check(brand.display.fallbackFontAssetTable.Contains(brand.body), "serif display falls back to the brand mono, never to LiberationSans");
            foreach (var path in new[] { "Fonts/InstrumentSerif-OFL.txt", "Fonts/GeistMono-OFL.txt" })
                Check(File.Exists(Path.Combine(ScalpalBrandBuild.Root, path)) && File.ReadAllText(Path.Combine(ScalpalBrandBuild.Root, path)).Contains("SIL Open Font License"), "OFL licence shipped beside the font: " + path);
            foreach (var material in new[] { brand.displayText, brand.bodyText, brand.labelText })
                Check(material.shader.name == "TextMeshPro/Mobile/Distance Field" && material.renderQueue == 3020 && material.mainTexture, "depth-tested text renders after glass and buttons: " + material.name);
            foreach (var material in new[] { brand.displayOverlay, brand.bodyOverlay })
                Check(material.shader.name == "TextMeshPro/Mobile/Distance Field Overlay" && material.renderQueue >= 4000, "transition title text renders above the stereo fade: " + material.name);
            Check(brand.glass.shader.name == "Scalpal/Brand/Glass" && brand.button.shader.name == "Scalpal/Brand/Glass" && brand.glass.renderQueue < brand.button.renderQueue && brand.button.renderQueue < brand.bodyText.renderQueue, "dark glass, then buttons, then text");
            Check(brand.glass.color.maxColorComponent < .05f && brand.glass.color.a > .7f, "cards are dark translucent glass");
            Check(brand.mark && brand.mark.mainTexture == brand.markTexture && brand.markTexture, "dither logo mark texture is bound");
            Check(brand.ray && brand.accent && ScalpalBrand.AccentOrange == (Color)new Color32(242, 77, 20, 255) && ScalpalBrand.AccentGold == (Color)new Color32(255, 219, 56, 255), "single warm accent from the site's spark");
        }

        static void ValidateStrings()
        {
            var brand = ScalpalBrand.Active;
            var literal = new Regex("\"((?:[^\"\\\\\\n]|\\\\.)*)\"");
            int strings = 0;
            foreach (var root in UiSources)
                foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                    foreach (Match match in literal.Matches(File.ReadAllText(file)))
                    {
                        string value = Regex.Unescape(match.Groups[1].Value); strings++;
                        Check(!Regex.IsMatch(value, "SCALPAL(?!_)") || value.StartsWith("SCALPAL_", StringComparison.Ordinal), "UI copy says \"Scalpal\", never \"SCALPAL\": " + Path.GetFileName(file) + " " + value);
                        foreach (char c in value.Where(c => c >= ' ' && c != '­'))
                        {
                            Check(brand.body.HasCharacter(c) || brand.body.HasCharacter(c, true), "body font covers '" + c + "' (U+" + ((int)c).ToString("X4") + ") from " + Path.GetFileName(file));
                            Check(brand.display.HasCharacter(c, true), "display font (with fallback) covers '" + c + "' from " + Path.GetFileName(file));
                        }
                    }
            Check(strings > 200, "string scan read the UI sources");
            Check(Shell.HubController.Wordmark == "Scalpal.", "launch wordmark is \"Scalpal.\" with the period");
        }

        static void ValidatePlacement()
        {
            var head = new GameObject("PlacementHead").transform; var panel = new GameObject("PlacementPanel").transform;
            try
            {
                foreach (var pose in new[] { Quaternion.Euler(35, 20, 18), Quaternion.Euler(-40, -70, -25), Quaternion.Euler(89, 10, 0), Quaternion.Euler(0, 180, 30) })
                {
                    head.SetPositionAndRotation(new Vector3(.3f, 1.6f, -.2f), pose);
                    Check(ScalpalPlacement.Place(panel, head, 1.2f, .06f), "placement resolves a facing for any head pose");
                    Check(ScalpalPlacement.IsLevel(panel), "panels spawn level with the horizon (no pitch/roll) for head " + pose.eulerAngles);
                    Check(Mathf.Abs(panel.position.y - (1.6f - .06f)) < 1e-4f, "panel height ignores head pitch");
                }
                head.rotation = Quaternion.Euler(10, 37, 25);
                ScalpalPlacement.Place(panel, head, 1.2f, 0);
                Check(Mathf.Abs(Mathf.DeltaAngle(panel.eulerAngles.y, 37)) < .01f, "yaw-only facing follows the head's heading");
            }
            finally { UnityEngine.Object.DestroyImmediate(head.gameObject); UnityEngine.Object.DestroyImmediate(panel.gameObject); }
        }

        static void ValidatePointer()
        {
            var root = new GameObject("PointerValidation");
            var previousOverride = ScalpalAim.Override; var previousClock = ScalpalAim.Clock;
            try
            {
                // A rotated, offset XR origin proves the ray is built in tracking space, not world space.
                var space = new GameObject("TrackingSpace").transform; space.SetParent(root.transform, false);
                space.SetPositionAndRotation(new Vector3(1.5f, 0, -2), Quaternion.Euler(0, 40, 0));
                ShellView.Configure(ScalpalBrand.Active);
                int presses = 0;
                var button = ShellView.Button(root.transform, "Probe", Vector3.zero, new Vector2(.3f, .08f), () => presses++);
                button.transform.SetPositionAndRotation(space.TransformPoint(new Vector3(0, 1.3f, 1.2f)), space.rotation);
                var other = ShellView.Panel(root.transform, "Blocker", Vector3.zero, new Vector2(.4f, .3f));
                other.gameObject.AddComponent<BoxCollider>().size = new Vector3(.4f, .3f, .01f);
                other.SetPositionAndRotation(space.TransformPoint(new Vector3(.8f, 1.3f, 1.2f)), space.rotation);
                Physics.SyncTransforms();
                var pointer = new ScalpalPointerHand(1, "ProbeRay");
                var tip = new Vector3(.18f, 1.05f, .25f);
                var target = space.InverseTransformPoint(button.transform.position);
                var aim = Quaternion.LookRotation(target - tip, Vector3.up) * Quaternion.Euler(0, 0, 25);
                float clock = 50, select = 0; bool valid = true; var position = tip; var rotation = aim;
                ScalpalAim.Clock = () => clock;
                ScalpalAim.Override = hand => hand == 1 && valid ? new ScalpalPointerSample { kind = ScalpalPointerKind.Controller, position = position, rotation = rotation, select = select } : default;
                Func<IScalpalPressable> step = () => { clock += .05f; return pointer.Step(space, true, root.transform); };
                step();
                Check((pointer.LastRay.origin - space.TransformPoint(tip)).magnitude < 1e-5f, "ray origin == aim pose position in tracking space");
                Check(Vector3.Angle(pointer.LastRay.direction, space.rotation * (aim * Vector3.forward)) < .001f, "ray direction == aim pose forward (roll-invariant)");
                var visual = pointer.Visual;
                Check(visual != null && visual.Visible && (visual.line.GetPosition(0) - pointer.LastRay.origin).magnitude < 1e-5f, "drawn ray starts at the controller tip");
                Physics.Raycast(pointer.LastRay, out var hit, 6);
                Check(hit.collider && hit.collider.gameObject == button.gameObject && (visual.line.GetPosition(1) - hit.point).magnitude < 1e-4f, "drawn ray ends at the hit point");
                Check(visual.reticle.gameObject.activeSelf && (visual.reticle.position - hit.point).magnitude < .005f, "reticle sits on the hit point");
                Check(pointer.Hovered as ShellButton == button && visual.Hovering && visual.line.startColor == ScalpalBrand.AccentOrange && visual.line.endColor == ScalpalBrand.AccentGold, "hovering a button takes the orange-to-gold accent");
                select = 1; step(); Check(presses == 1, "trigger pull presses the hovered button once");
                select = 0; step(); select = 1; step(); Check(presses == 1, "release and re-press inside 150 ms is debounced");
                step(); step(); Check(presses == 1, "a held trigger does not repeat");
                select = 0; step(); select = 1; step(); Check(presses == 2, "deliberate second press after the debounce presses again");
                select = 0; step();
                rotation = Quaternion.LookRotation(space.InverseTransformPoint(other.position) - tip, Vector3.up); step();
                Check(pointer.Hovered == null && visual.Visible && !visual.Hovering && visual.reticle.gameObject.activeSelf, "non-button surfaces stop the ray with a neutral reticle");
                rotation = Quaternion.LookRotation(Vector3.up + Vector3.forward * .1f, Vector3.forward); step();
                Check(visual.Visible && !visual.reticle.gameObject.activeSelf && Mathf.Abs(Vector3.Distance(visual.line.GetPosition(0), visual.line.GetPosition(1)) - ScalpalRayVisual.MissLength) < 1e-3f, "a miss draws a short ray without a reticle");
                valid = false; step(); Check(!visual.Visible && !visual.reticle.gameObject.activeSelf, "tracking loss hides the ray");
                select = 1; valid = true; rotation = aim; step(); Check(visual.Visible, "tracking recovery shows the ray again");
                step(); Check(presses == 2, "a trigger already held through tracking recovery cannot press");
                select = 0; step();
                pointer.Step(space, false, root.transform); Check(!visual.Visible, "blocked input (transition/pause) hides the ray");
                step(); clock += ScalpalPointerHand.IdleSeconds + 1; step();
                Check(!visual.Visible && pointer.Idle, "a controller set down (no motion, no input) hides its ray");
                position = tip + Vector3.right * .01f; step(); Check(visual.Visible && !pointer.Idle, "picking the controller up shows the ray again");
                ScalpalAim.Override = hand => default; step();
                Check(!visual.Visible, "an absent controller has no ray");
                pointer.Destroy();
            }
            finally { ScalpalAim.Override = previousOverride; ScalpalAim.Clock = previousClock; UnityEngine.Object.DestroyImmediate(root); }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Brand validation: " + message);
            checks++;
        }
    }
}
