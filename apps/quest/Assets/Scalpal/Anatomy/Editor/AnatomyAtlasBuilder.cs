using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Anatomy.EditorTools
{
    /// <summary>
    /// Builds an inspection prefab from the checked-in atlas and FBX meshes.
    /// Imported coordinates are preserved. This is not participant registration.
    /// </summary>
    public static class AnatomyAtlasBuilder
    {
        const string AnatomyPath = "Assets/Scalpal/Anatomy";
        const string ManifestPath = AnatomyPath + "/Resources/anatomy-atlas.json";
        public const string PrefabPath = AnatomyPath + "/Prefabs/AnatomyAtlas.prefab";

        [Serializable]
        sealed class Atlas
        {
            public int schemaVersion;
            public SystemEntry[] systems;
            public PartEntry[] parts;
        }

        [Serializable]
        sealed class SystemEntry
        {
            public string id;
            public string assetPath;
            public string group;
        }

        [Serializable]
        sealed class PartEntry
        {
            public string stableId;
            public string displayName;
            public string system;
            public string objectName;
            public string catalogId;
        }

        [MenuItem("Scalpal/Anatomy/Build Atlas Prefab")]
        public static void BuildFromMenu()
        {
            try
            {
                var prefab = Build();
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
            }
            catch (Exception error)
            {
                Debug.LogError("[Scalpal anatomy] Atlas build failed: " + error.Message);
            }
        }

        // Public for Unity batch mode. Exceptions deliberately propagate to fail automation.
        public static GameObject Build()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (text == null) throw new InvalidOperationException("Missing atlas manifest: " + ManifestPath);
            var atlas = JsonUtility.FromJson<Atlas>(text.text);
            ValidateManifest(atlas);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new InvalidOperationException("URP Lit shader is unavailable. Import into the team's URP Unity project first.");

            var bodySystems = atlas.systems.Where(s => s.group == "body").ToArray();
            if (bodySystems.Length == 0) throw new InvalidOperationException("Atlas has no body systems.");
            var bodyIds = new HashSet<string>(bodySystems.Select(s => s.id), StringComparer.Ordinal);
            var body = BuildAtlas(new Atlas {
                schemaVersion = atlas.schemaVersion,
                systems = bodySystems,
                parts = atlas.parts.Where(p => bodyIds.Contains(p.system)).ToArray()
            }, shader, "AnatomyAtlas_Preview", PrefabPath);
            // Detailed atlases do not share the full body's coordinate frame. Keep each
            // in its own prefab for inspection; never silently position it inside the body.
            foreach (var detail in atlas.systems.Where(s => s.group == "detail"))
            {
                BuildAtlas(new Atlas {
                    schemaVersion = atlas.schemaVersion,
                    systems = new[] { detail },
                    parts = atlas.parts.Where(p => p.system == detail.id).ToArray()
                }, shader, detail.id + "_Preview", AnatomyPath + "/Prefabs/" + detail.id + ".prefab");
            }
            return body;
        }

        static GameObject BuildAtlas(Atlas atlas, Shader shader, string rootName, string prefabPath)
        {
            var root = new GameObject(rootName);
            try
            {
                var instances = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                var meshes = new Dictionary<string, Dictionary<string, MeshFilter>>(StringComparer.Ordinal);
                foreach (var system in atlas.systems)
                {
                    ConfigureModelImport(system.assetPath);
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(system.assetPath);
                    if (model == null) throw new InvalidOperationException("Missing model: " + system.assetPath);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
                    if (instance == null) throw new InvalidOperationException("Cannot instantiate " + system.assetPath);
                    // Unpack so identity components, catalog names and materials are saved in this prefab.
                    PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    instance.name = system.id;
                    instances.Add(system.id, instance);
                    var systemMeshes = new Dictionary<string, MeshFilter>(StringComparer.Ordinal);
                    foreach (var filter in instance.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (filter.sharedMesh == null) continue;
                        if (filter.GetComponent<Renderer>() == null)
                            throw new InvalidOperationException(system.id + "/" + filter.name + " has a mesh but no renderer.");
                        if (systemMeshes.ContainsKey(filter.name))
                            throw new InvalidOperationException("Ambiguous mesh object name in " + system.id + ": " + filter.name);
                        systemMeshes.Add(filter.name, filter);
                    }
                    if (systemMeshes.Count == 0) throw new InvalidOperationException("No static meshes in " + system.assetPath);
                    meshes.Add(system.id, systemMeshes);
                }

                // Validate complete coverage before changing any generated assets.
                foreach (var part in atlas.parts)
                    if (!meshes[part.system].ContainsKey(part.objectName))
                        throw new InvalidOperationException("Manifest part " + part.stableId + " cannot find mesh " + part.system + "/" + part.objectName);
                foreach (var system in atlas.systems)
                {
                    var named = new HashSet<string>(atlas.parts.Where(p => p.system == system.id).Select(p => p.objectName), StringComparer.Ordinal);
                    var unmapped = meshes[system.id].Keys.Where(name => !named.Contains(name)).ToArray();
                    if (unmapped.Length != 0)
                        throw new InvalidOperationException(system.id + " has meshes absent from the manifest: " + string.Join(", ", unmapped.Take(10)));
                }

                EnsureFolder(AnatomyPath + "/Materials");
                EnsureFolder(AnatomyPath + "/Prefabs");
                var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
                var ghostMaterials = new Dictionary<string, Material>(StringComparer.Ordinal);
                foreach (var part in atlas.parts)
                {
                    string key = MaterialKey(part.system, part.displayName);
                    if (!materials.TryGetValue(key, out var material))
                    {
                        material = BuildMaterial(key, shader);
                        materials.Add(key, material);
                        ghostMaterials.Add(key, BuildMaterial(key, shader, true));
                    }
                    var renderer = meshes[part.system][part.objectName].GetComponent<Renderer>();
                    int slots = Math.Max(1, renderer.sharedMaterials.Length);
                    renderer.sharedMaterials = Enumerable.Repeat(material, slots).ToArray();
                }

                int colliders = 0;
                int anatomyLayer = LayerMask.NameToLayer("Anatomy");
                foreach (var part in atlas.parts)
                {
                    var filter = meshes[part.system][part.objectName];
                    var component = filter.gameObject.AddComponent<AnatomyPart>();
                    component.stableId = part.stableId;
                    component.displayName = part.displayName;
                    component.system = part.system;
                    component.ghostMaterial = ghostMaterials[MaterialKey(part.system, part.displayName)];
                    if (string.IsNullOrEmpty(part.catalogId)) continue;
                    filter.name = "anat_" + part.catalogId;
                    // Only authored exercise targets get physics. Thousands of whole-body
                    // colliders are unnecessary; static concave geometry needs no Rigidbody.
                    var collider = filter.gameObject.GetComponent<MeshCollider>();
                    if (collider == null) collider = filter.gameObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = filter.sharedMesh;
                    collider.convex = false;
                    collider.isTrigger = false;
                    if (anatomyLayer >= 0) filter.gameObject.layer = anatomyLayer;
                    colliders++;
                }

                var controller = root.AddComponent<AnatomyController>();
                controller.initiallyHiddenSystems = atlas.systems.Where(s => s.group == "body" && (s.id == "surface" || s.id == "skin"))
                    .Select(s => s.id).ToArray();
                controller.RebuildIndex();
                controller.SetPreviewMode(true);
                // Store authored visibility, not this editor instance's transient filters.
                // AnatomyPart captures these defaults on Awake; the controller then applies
                // initiallyHiddenSystems. Serializing a disabled renderer would make the
                // surface impossible to reveal in the running scene.
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) renderer.enabled = true;
                foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = true;
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
                if (!success || prefab == null) throw new InvalidOperationException("Unity could not save " + prefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[Scalpal anatomy] Saved " + prefabPath + ": " + atlas.parts.Length + " parts, "
                    + atlas.systems.Length + " systems, " + colliders + " catalog colliders. "
                    + "Imported coordinates are preserved. Participant alignment and Quest performance are unverified.");
                if (colliders > 0 && anatomyLayer < 0)
                    Debug.LogWarning("[Scalpal anatomy] Anatomy physics layer is missing. Catalog colliders remain on their imported layers; configure layers during scene integration.");
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void ValidateManifest(Atlas atlas)
        {
            if (atlas == null || atlas.schemaVersion != 1 || atlas.systems == null || atlas.systems.Length == 0
                || atlas.parts == null || atlas.parts.Length == 0)
                throw new InvalidOperationException("Atlas must use schemaVersion 1 and contain systems and parts.");
            var systems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var system in atlas.systems)
            {
                if (system == null || string.IsNullOrWhiteSpace(system.id)
                    || !system.id.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-') || !systems.Add(system.id))
                    throw new InvalidOperationException("Atlas system IDs must be unique filename-safe names.");
                if (system.group != "body" && system.group != "detail")
                    throw new InvalidOperationException("System " + system.id + " must have group body or detail.");
                if (string.IsNullOrEmpty(system.assetPath) || !system.assetPath.StartsWith(AnatomyPath + "/Models/", StringComparison.Ordinal)
                    || system.assetPath.Contains("..") || system.assetPath.Contains("\\")
                    || !system.assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("System " + system.id + " must reference an FBX under " + AnatomyPath + "/Models/.");
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var objects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in atlas.parts)
            {
                if (part == null || string.IsNullOrWhiteSpace(part.stableId) || !ids.Add(part.stableId)
                    || string.IsNullOrWhiteSpace(part.displayName) || string.IsNullOrWhiteSpace(part.objectName)
                    || string.IsNullOrEmpty(part.system) || !systems.Contains(part.system)
                    || !objects.Add(part.system + "\n" + part.objectName))
                    throw new InvalidOperationException("Atlas parts need unique IDs and system/objectName pairs, display names, and a known system.");
                if (!string.IsNullOrEmpty(part.catalogId) && part.stableId != part.catalogId)
                    throw new InvalidOperationException("Catalog part " + part.catalogId + " must use that catalog ID as its stableId.");
            }
        }

        static Material BuildMaterial(string system, Shader shader, bool ghost = false)
        {
            string name = system + (ghost ? "_ghost" : "");
            string path = AnatomyPath + "/Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool isNew = material == null;
            if (isNew) material = new Material(shader);
            material.shader = shader;
            material.name = name;
            var color = SystemColor(system);
            color.a = ghost ? 0.15f : 1f;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", 0.3f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Surface", ghost ? 1f : 0f);
            material.SetFloat("_ZWrite", ghost ? 0f : 1f);
            material.SetFloat("_SrcBlend", ghost ? (float)UnityEngine.Rendering.BlendMode.SrcAlpha : (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", ghost ? (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetOverrideTag("RenderType", ghost ? "Transparent" : "Opaque");
            material.SetShaderPassEnabled("ShadowCaster", !ghost);
            material.SetColor("_EmissionColor", Color.black);
            material.EnableKeyword("_EMISSION");
            if (ghost) material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            else material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = ghost ? 3000 : -1;
            if (isNew) AssetDatabase.CreateAsset(material, path);
            else EditorUtility.SetDirty(material);
            return material;
        }

        static void ConfigureModelImport(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("Missing FBX model importer: " + path);
            if (importer.bakeAxisConversion && importer.useFileScale && importer.globalScale == 1f
                && !importer.importAnimation && importer.animationType == ModelImporterAnimationType.None
                && importer.materialImportMode == ModelImporterMaterialImportMode.None) return;
            // Blender exports -Z forward, Y up, meter units. Bake the FBX conversion
            // without inventing a torso origin or moving any system independently.
            importer.bakeAxisConversion = true;
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.SaveAndReimport();
        }

        static Color SystemColor(string id)
        {
            string value = id.ToLowerInvariant();
            if (value == "tissue_liver") return new Color(0.38f, 0.1f, 0.12f);
            if (value == "tissue_lung") return new Color(0.73f, 0.47f, 0.49f);
            if (value == "tissue_duct") return new Color(0.28f, 0.57f, 0.3f);
            if (value == "tissue_kidney") return new Color(0.47f, 0.22f, 0.16f);
            if (value.Contains("skelet") || value.Contains("bone")) return new Color(0.88f, 0.84f, 0.7f);
            if (value.Contains("nerv")) return new Color(1f, 0.78f, 0.16f);
            if (value.Contains("lymph")) return new Color(0.4f, 0.72f, 0.42f);
            if (value.Contains("cardio") || value.Contains("vascul") || value.Contains("arter")) return new Color(0.78f, 0.13f, 0.19f);
            if (value.Contains("vein")) return new Color(0.2f, 0.36f, 0.75f);
            if (value.Contains("musc")) return new Color(0.62f, 0.22f, 0.23f);
            if (value.Contains("joint") || value.Contains("ligament") || value.Contains("articul")) return new Color(0.7f, 0.8f, 0.83f);
            if (value.Contains("skin") || value.Contains("surface")) return new Color(0.76f, 0.57f, 0.43f);
            return new Color(0.72f, 0.39f, 0.38f);
        }

        // A bounded palette shared across all meshes. These are teaching colors,
        // selected from source names, not a simulation of oxygenation or perfusion.
        static string MaterialKey(string system, string displayName)
        {
            string name = (displayName ?? "").ToLowerInvariant();
            if (name.Contains("vein")) return "vessel_vein";
            if (name.Contains("artery") || name.Contains("arteries")) return "vessel_artery";
            if (name.Contains("duct")) return "tissue_duct";
            if (name.Contains("liver")) return "tissue_liver";
            if (name.Contains("lung")) return "tissue_lung";
            if (name.Contains("kidney")) return "tissue_kidney";
            return system;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
