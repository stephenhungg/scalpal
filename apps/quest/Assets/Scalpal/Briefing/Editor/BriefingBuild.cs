using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Briefing.Editor
{
    /// <summary>
    /// Import contract for the merged briefing atlas: readable (the picker reads triangles on device), no mesh
    /// optimization (it reorders triangles and breaks triStart/triCount), no lightmap UVs (they overwrite uv2, the
    /// part index), 32-bit indices, no materials or animation.
    /// </summary>
    public sealed class BriefingAtlasImport : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            if (assetPath != BriefingBuild.ModelPath) return;
            var importer = (ModelImporter)assetImporter;
            importer.isReadable = true;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.meshOptimizationFlags = 0;
            importer.optimizeMeshPolygons = false; importer.optimizeMeshVertices = false;
            importer.generateSecondaryUV = false;
            importer.weldVertices = false;
            importer.indexFormat = ModelImporterIndexFormat.UInt32;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importBlendShapes = false; importer.importCameras = false; importer.importLights = false;
            importer.importAnimation = false; importer.animationType = ModelImporterAnimationType.None;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
        }
    }

    public static class BriefingBuild
    {
        public const string Root = "Assets/Scalpal/Briefing";
        public const string ModelPath = Root + "/Models/briefing_atlas.fbx";
        public const string ShaderPath = Root + "/Shaders/BriefingAtlas.shader";
        public const string MaterialPath = Root + "/Materials/BriefingAtlas.mat";
        public const string VoidShaderPath = Root + "/Shaders/BriefingVoid.shader", VoidMaterialPath = Root + "/Materials/BriefingVoid.mat";
        public const string MoteShaderPath = Root + "/Shaders/BriefingMote.shader", MoteMaterialPath = Root + "/Materials/BriefingMote.mat";
        public const string SourcePath = Root + "/Resources/" + BriefingAtlas.SourceResource + ".asset";
        public const string VoicePath = Root + "/Resources/BriefingVoice";

        /// <summary>Creates/refreshes the material and the Resources handle that points at the imported atlas mesh.</summary>
        [MenuItem("Scalpal/Briefing/Prepare Atlas")]
        public static BriefingAtlasSource Prepare()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (!shader) throw new System.InvalidOperationException("Briefing shader missing: " + ShaderPath);
            Directory.CreateDirectory(Path.GetDirectoryName(MaterialPath));
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (!material) { material = new Material(shader) { name = "BriefingAtlas" }; AssetDatabase.CreateAsset(material, MaterialPath); }
            material.shader = shader; material.enableInstancing = true;
            var source = AssetDatabase.LoadAssetAtPath<BriefingAtlasSource>(SourcePath);
            if (!source) { source = ScriptableObject.CreateInstance<BriefingAtlasSource>(); AssetDatabase.CreateAsset(source, SourcePath); }
            source.material = material;
            source.voidMaterial = BackdropMaterial(VoidShaderPath, VoidMaterialPath);
            source.moteMaterial = BackdropMaterial(MoteShaderPath, MoteMaterialPath);
            source.mesh = AssetDatabase.LoadAllAssetsAtPath(ModelPath).OfType<Mesh>().FirstOrDefault();
            // Bundled lines play through QuestJarvisVoice.PlayLocalSpeech, which reads samples: decompress on load.
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { VoicePath }))
            {
                var importer = (AudioImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
                var settings = importer.defaultSampleSettings;
                if (settings.loadType == AudioClipLoadType.DecompressOnLoad && importer.forceToMono) continue;
                settings.loadType = AudioClipLoadType.DecompressOnLoad; importer.defaultSampleSettings = settings; importer.forceToMono = true;
                importer.SaveAndReimport();
            }
            EditorUtility.SetDirty(material); EditorUtility.SetDirty(source); AssetDatabase.SaveAssets();
            return source;
        }

        // Referenced from the Resources handle, so both shaders ship in the player without Shader.Find.
        static Material BackdropMaterial(string shaderPath, string materialPath)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            if (!shader) throw new System.InvalidOperationException("Briefing backdrop shader missing: " + shaderPath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (!material) { material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) }; AssetDatabase.CreateAsset(material, materialPath); }
            material.shader = shader; material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
