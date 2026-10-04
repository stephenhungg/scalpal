using System;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Scalpal.Brand.Editor
{
    /// <summary>
    /// Generates the brand's static SDF font assets, text/surface materials and the shared
    /// Resources/ScalpalBrand asset. Deterministic and idempotent; run before any scene build.
    /// </summary>
    public static class ScalpalBrandBuild
    {
        public const string Root = "Assets/Scalpal/Brand";
        public const string BrandPath = Root + "/Resources/" + ScalpalBrand.ResourcePath + ".asset";
        public const int SamplingPointSize = 72, Padding = 8;
        static readonly Vector2Int[] AtlasSizes = { new Vector2Int(1024, 1024), new Vector2Int(2048, 1024), new Vector2Int(2048, 2048) };

        /// <summary>Every glyph any Scalpal surface may show: ASCII, Latin-1, Latin Extended-A and UI punctuation.</summary>
        public static string CharacterSet()
        {
            var set = new StringBuilder();
            for (int c = 32; c < 127; c++) set.Append((char)c);
            for (int c = 0xA0; c <= 0x17F; c++) if (c != 0xAD) set.Append((char)c);
            set.Append("–—‘’‚“”„•…‹›−→←↑↓±×≥≤√");
            return set.ToString();
        }

        [MenuItem("Scalpal/Brand/Prepare Brand Assets")]
        public static ScalpalBrand Prepare()
        {
            if (!AssetDatabase.LoadAssetAtPath<TMP_Settings>("Assets/TextMesh Pro/Resources/TMP Settings.asset"))
                throw new InvalidOperationException("TMP Essential Resources are missing from Assets/TextMesh Pro.");
            Directory.CreateDirectory(Root + "/Fonts/SDF"); Directory.CreateDirectory(Root + "/Materials"); Directory.CreateDirectory(Root + "/Resources");
            AssetDatabase.Refresh();
            ConfigureMarkTexture();
            var body = FontAsset("GeistMono-Regular", "Geist Mono SDF");
            var label = FontAsset("GeistMono-Medium", "Geist Mono Medium SDF");
            var display = FontAsset("InstrumentSerif-Regular", "Instrument Serif SDF");
            // Instrument Serif lacks arrows/maths; fall back to the mono body face, never to LiberationSans.
            display.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset> { body };
            label.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset> { body };
            body.fallbackFontAssetTable = new System.Collections.Generic.List<TMP_FontAsset>();
            foreach (var font in new[] { body, label, display }) EditorUtility.SetDirty(font);

            var brand = AssetDatabase.LoadAssetAtPath<ScalpalBrand>(BrandPath);
            if (!brand) { brand = ScriptableObject.CreateInstance<ScalpalBrand>(); AssetDatabase.CreateAsset(brand, BrandPath); }
            brand.display = display; brand.body = body; brand.label = label;
            brand.displayText = TextMaterial("brand_text_display", display, false);
            brand.bodyText = TextMaterial("brand_text_body", body, false);
            brand.labelText = TextMaterial("brand_text_label", label, false);
            brand.displayOverlay = TextMaterial("brand_text_display_overlay", display, true);
            brand.bodyOverlay = TextMaterial("brand_text_body_overlay", body, true);
            brand.glass = Glass("brand_glass", ScalpalBrand.GlassTint, .16f, 3000);
            brand.button = Glass("brand_button", ScalpalBrand.ButtonTint, .5f, 3010);
            brand.accent = Unlit("brand_accent", ScalpalBrand.Accent, null, 3015);
            brand.ray = Unlit("brand_ray", Color.white, null, 3030);
            brand.markTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/ScalpalMark.png");
            brand.mark = Unlit("brand_mark", Color.white, brand.markTexture, 3020);
            EditorUtility.SetDirty(brand);
            AssetDatabase.SaveAssets();
            Debug.Log("SCALPAL_BRAND_PREPARED display=" + display.name + " body=" + body.name + " label=" + label.name +
                " atlases=" + string.Join(",", new[] { display, body, label }.Select(f => f.atlasWidth + "x" + f.atlasHeight + ":" + f.characterTable.Count)));
            return brand;
        }

        static void ConfigureMarkTexture()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(Root + "/Textures/ScalpalMark.png");
            if (importer == null) throw new InvalidOperationException("Missing Brand/Textures/ScalpalMark.png");
            bool changed = importer.textureType != TextureImporterType.Default || importer.alphaIsTransparency != true || importer.mipmapEnabled != true || importer.wrapMode != TextureWrapMode.Clamp;
            importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = true; importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp; importer.sRGBTexture = true; importer.filterMode = FilterMode.Trilinear;
            if (changed) importer.SaveAndReimport();
        }

        static TMP_FontAsset FontAsset(string file, string assetName)
        {
            string path = Root + "/Fonts/SDF/" + assetName + ".asset";
            var source = AssetDatabase.LoadAssetAtPath<Font>(Root + "/Fonts/" + file + ".ttf");
            if (!source) throw new InvalidOperationException("Missing brand font " + file + ".ttf");
            string wanted = CharacterSet();
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (existing && existing.atlasPopulationMode == AtlasPopulationMode.Static && existing.atlasTexture && existing.material &&
                existing.creationSettings.sourceFontFileGUID == AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source)) &&
                existing.creationSettings.characterSequence == wanted)
                return existing; // unchanged: keep GUIDs and avoid atlas churn.
            if (existing) AssetDatabase.DeleteAsset(path);
            TMP_FontAsset font = null;
            foreach (var size in AtlasSizes)
            {
                font = TMP_FontAsset.CreateFontAsset(source, SamplingPointSize, Padding, GlyphRenderMode.SDFAA, size.x, size.y, AtlasPopulationMode.Dynamic, false);
                if (!font) throw new InvalidOperationException("TextCore could not load " + file);
                font.TryAddCharacters(wanted, out string missing);
                var cmapMissing = missing ?? "";
                // Characters the face genuinely lacks are expected; atlas overflow is not.
                bool overflow = cmapMissing.Any(c => HasGlyph(source, c));
                if (!overflow) break;
                UnityEngine.Object.DestroyImmediate(font.atlasTexture); UnityEngine.Object.DestroyImmediate(font.material); UnityEngine.Object.DestroyImmediate(font);
                font = null;
            }
            if (!font) throw new InvalidOperationException("Brand font atlas overflow: " + file);
            font.name = assetName;
            var settings = font.creationSettings;
            settings.sourceFontFileGUID = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            settings.pointSize = SamplingPointSize; settings.padding = Padding; settings.atlasWidth = font.atlasWidth; settings.atlasHeight = font.atlasHeight;
            settings.characterSetSelectionMode = 7; settings.characterSequence = wanted; settings.renderMode = (int)GlyphRenderMode.SDFAA; settings.packingMode = 4;
            font.creationSettings = settings;
            // Static: the atlas is baked now; nothing is rasterised on Quest.
            font.atlasPopulationMode = AtlasPopulationMode.Static;
            AssetDatabase.CreateAsset(font, path);
            font.atlasTexture.name = assetName + " Atlas";
            font.material.name = assetName + " Material";
            AssetDatabase.AddObjectToAsset(font.atlasTexture, font);
            AssetDatabase.AddObjectToAsset(font.material, font);
            EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
        }

        static bool HasGlyph(Font source, char c)
        {
            FontEngine.LoadFontFace(source, SamplingPointSize);
            return FontEngine.TryGetGlyphIndex(c, out uint index) && index != 0;
        }

        static Material TextMaterial(string name, TMP_FontAsset font, bool overlay)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!material) { material = new Material(font.material); AssetDatabase.CreateAsset(material, path); }
            material.CopyPropertiesFromMaterial(font.material);
            material.shader = Shader.Find(overlay ? "TextMeshPro/Mobile/Distance Field Overlay" : "TextMeshPro/Mobile/Distance Field");
            if (!material.shader) throw new InvalidOperationException("TMP mobile SDF shader missing");
            material.SetTexture(ShaderUtilities.ID_MainTex, font.atlasTexture);
            material.SetColor(ShaderUtilities.ID_FaceColor, Color.white);
            // Text draws after glass (3000) and buttons (3010); overlays sit above the stereo fade.
            material.renderQueue = overlay ? 4200 : 3020;
            material.enableInstancing = false;
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Glass(string name, Color fill, float radiusFraction, int queue)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Scalpal/Brand/Glass");
            if (!shader) throw new InvalidOperationException("Scalpal/Brand/Glass shader missing");
            if (!material) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader; material.color = fill;
            material.SetColor("_RimColor", ScalpalBrand.Hairline); material.SetColor("_RimColorB", ScalpalBrand.Hairline);
            material.SetFloat("_RimWidth", .0011f); material.SetFloat("_RimStrength", 1); material.SetFloat("_RadiusMax", .05f);
            material.SetFloat("_RadiusFraction", radiusFraction); material.SetFloat("_Glow", 0);
            material.renderQueue = queue; material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        static Material Unlit(string name, Color color, Texture texture, int queue)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Scalpal/Brand/Unlit");
            if (!shader) throw new InvalidOperationException("Scalpal/Brand/Unlit shader missing");
            if (!material) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader; material.color = color; material.mainTexture = texture; material.renderQueue = queue; material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
