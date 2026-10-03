using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Scalpal.Exercises.Data;

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
        public const string DefaultProcedureId = "lap_appendectomy";
        public const string AppendectomyPrefabPath = AnatomyPath + "/Prefabs/AnatomyExercise_lap_appendectomy.prefab";
        public const string OrganOverviewPrefabPath = AnatomyPath + "/Prefabs/AnatomyOrgans_Overview.prefab";
        public const int OrganOverviewExpectedTriangles = 110511;
        public const int OrganOverviewTriangleBudget = 150000;
        public static int OrganOverviewPartCount => OrganOverviewIds.Count;

        // Explicit shared-body-frame parts, including the five lung lobes and four heart
        // chambers as their source IDs. Do not invent combined "heart"/"lungs" surgery IDs.
        // Greater omentum is deliberately omitted: its 77,968 triangles cover anterior
        // organs. BuildExercise still preserves that authored appendectomy context.
        public static readonly IReadOnlyList<string> OrganOverviewIds = Array.AsReadOnly(new[] {
            "liver", "gallbladder", "pancreas", "stomach", "duodenum", "small_bowel",
            "cecum", "terminal_ileum", "appendix", "mesoappendix", "visceral__ascending_colon",
            "transverse_colon", "descending_colon", "sigmoid_colon", "rectum",
            "sigmoid_mesocolon", "left_gonadal_vessels", "cystic_duct", "cystic_artery",
            "common_bile_duct", "common_hepatic_duct", "right_hepatic_artery", "appendicular_artery",
            "left_kidney", "right_kidney", "left_ureter", "right_ureter", "urinary_bladder",
            "visceral__suprarenal_gland_l", "visceral__suprarenal_gland_r", "lymphatic__spleen",
            "visceral__oesophagus", "visceral__trachea",
            "visceral__superior_lobe_of_left_lung", "visceral__inferior_lobe_of_left_lung",
            "visceral__superior_lobe_of_right_lung", "visceral__middle_lobe_of_right_lung",
            "visceral__inferior_lobe_of_right_lung",
            "cardiovascular__left_atrium", "cardiovascular__right_atrium",
            "cardiovascular__left_ventricle", "cardiovascular__right_ventricle"
        });

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
            public int triangles;
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
            var shader = FindShader();

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

        [MenuItem("Scalpal/Anatomy/Build Appendectomy Prefab")]
        public static void BuildAppendectomyFromMenu()
        {
            Selection.activeObject = BuildExercise();
        }

        // Builds only the authored case's available context and required interaction targets.
        // No source-to-patient rotation, origin shift or registration is inferred here.
        public static GameObject BuildExercise(string procedureId = DefaultProcedureId)
        {
            if (string.IsNullOrWhiteSpace(procedureId) || !procedureId.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                throw new InvalidOperationException("Procedure ID must be a filename-safe catalog ID.");
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (text == null) throw new InvalidOperationException("Missing atlas manifest: " + ManifestPath);
            var atlas = JsonUtility.FromJson<Atlas>(text.text);
            ValidateManifest(atlas);
            var bundleAsset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
            if (bundleAsset == null) throw new InvalidOperationException("Missing packaged exercise bundle.");
            var bundle = JsonUtility.FromJson<ScalpalBundle>(bundleAsset.text);
            var procedures = (bundle?.procedures ?? Array.Empty<Procedure>()).Where(p => p != null && p.id == procedureId).ToArray();
            if (procedures.Length != 1 || procedures[0].steps == null || procedures[0].steps.Length == 0)
                throw new InvalidOperationException("Procedure must exist exactly once in the packaged bundle: " + procedureId);
            var procedure = procedures[0];
            var required = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in procedure.steps)
            {
                if (step == null || step.check == null) throw new InvalidOperationException("Procedure contains an invalid step.");
                if (step.check.type == "touch_target" || step.check.type == "identify_targets" || step.check.type == "apply_count")
                {
                    required.UnionWith(step.targets ?? Array.Empty<string>());
                    required.UnionWith(step.check.targets ?? Array.Empty<string>());
                }
                foreach (var mistake in step.mistakes ?? Array.Empty<StepMistake>())
                    if (mistake != null) required.Add(mistake.structure);
            }
            var bodySystems = new HashSet<string>(atlas.systems.Where(x => x.group == "body").Select(x => x.id), StringComparer.Ordinal);
            var available = atlas.parts.Where(p => bodySystems.Contains(p.system)).ToDictionary(p => p.stableId, StringComparer.Ordinal);
            foreach (var id in required)
                if (string.IsNullOrWhiteSpace(id) || !available.TryGetValue(id, out var part) || part.catalogId != id)
                    throw new InvalidOperationException("Required interaction target is absent or not collider-addressable: " + (id ?? "<empty>"));
            var displayed = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (var id in procedure.structures ?? Array.Empty<string>())
                if (id != null && available.ContainsKey(id)) displayed.Add(id);
            if (displayed.Count == 0) throw new InvalidOperationException("Exercise contains no available anatomy.");
            var selected = atlas.parts.Where(p => displayed.Contains(p.stableId)).ToArray();
            var systems = new HashSet<string>(selected.Select(p => p.system), StringComparer.Ordinal);
            var subset = new Atlas {
                schemaVersion = atlas.schemaVersion,
                systems = atlas.systems.Where(x => systems.Contains(x.id)).ToArray(),
                parts = selected
            };
            return BuildAtlas(subset, FindShader(), "AnatomyExercise_" + procedureId,
                AnatomyPath + "/Prefabs/AnatomyExercise_" + procedureId + ".prefab", false);
        }

        [MenuItem("Scalpal/Anatomy/Build Organ Overview Prefab")]
        public static void BuildOrganOverviewFromMenu()
        {
            Selection.activeObject = BuildOrganOverview();
        }

        // Reusable selection/inspection anatomy, never another scored case. All selected
        // geometry retains its source frame; the caller centers/scales the pedestal only.
        // Independent HRA detail assets and the full skin/skeleton atlas remain separate.
        public static GameObject BuildOrganOverview()
        {
            var text = AssetDatabase.LoadAssetAtPath<TextAsset>(ManifestPath);
            if (text == null) throw new InvalidOperationException("Missing atlas manifest: " + ManifestPath);
            var atlas = JsonUtility.FromJson<Atlas>(text.text);
            ValidateManifest(atlas);
            var available = atlas.parts.ToDictionary(part => part.stableId, StringComparer.Ordinal);
            var bodySystems = new HashSet<string>(atlas.systems.Where(system => system.group == "body")
                .Select(system => system.id), StringComparer.Ordinal);
            var selected = new List<PartEntry>();
            foreach (var id in OrganOverviewIds)
            {
                if (!available.TryGetValue(id, out var part) || !bodySystems.Contains(part.system) || part.triangles <= 0)
                    throw new InvalidOperationException("Organ overview requires verified shared-frame body geometry: " + id);
                selected.Add(part);
            }
            long triangles = selected.Sum(part => (long)part.triangles);
            if (triangles != OrganOverviewExpectedTriangles || triangles > OrganOverviewTriangleBudget)
                throw new InvalidOperationException("Organ overview geometry changed: " + triangles
                    + " triangles; expected " + OrganOverviewExpectedTriangles + ", budget " + OrganOverviewTriangleBudget + ".");
            var systems = new HashSet<string>(selected.Select(part => part.system), StringComparer.Ordinal);
            var subset = new Atlas {
                schemaVersion = atlas.schemaVersion,
                systems = atlas.systems.Where(system => systems.Contains(system.id)).ToArray(),
                parts = selected.ToArray()
            };
            // No collider is authored into this overview, including catalog-named organs.
            // Practice physics continue to come exclusively from BuildExercise.
            return BuildAtlas(subset, FindShader(), "AnatomyOrgans_Overview", OrganOverviewPrefabPath, false, false);
        }

        static Shader FindShader()
        {
            var shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null
                ? Shader.Find("Standard") : Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Neither Standard nor the active URP Lit shader is available.");
            return shader;
        }

        static GameObject BuildAtlas(Atlas atlas, Shader shader, string rootName, string prefabPath,
            bool requireCompleteCoverage = true, bool addCatalogColliders = true)
        {
            var root = new GameObject(rootName);
            try
            {
                var models = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                var meshes = new Dictionary<string, Dictionary<string, MeshFilter>>(StringComparer.Ordinal);
                foreach (var system in atlas.systems)
                {
                    ConfigureModelImport(system.assetPath);
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(system.assetPath);
                    if (model == null) throw new InvalidOperationException("Missing model: " + system.assetPath);
                    models.Add(system.id, model);
                    var systemMeshes = new Dictionary<string, MeshFilter>(StringComparer.Ordinal);
                    foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
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
                    if (requireCompleteCoverage && unmapped.Length != 0)
                        throw new InvalidOperationException(system.id + " has meshes absent from the manifest: " + string.Join(", ", unmapped.Take(10)));
                }

                // Inspect asset transforms first, then create only selected mesh nodes and their
                // ancestors. This avoids instantiating thousands of unused source GameObjects.
                foreach (var system in atlas.systems)
                {
                    var model = models[system.id];
                    var sourceMeshes = meshes[system.id];
                    var copied = new Dictionary<Transform, Transform>();
                    var selectedMeshes = new Dictionary<string, MeshFilter>(StringComparer.Ordinal);
                    foreach (var part in atlas.parts.Where(p => p.system == system.id))
                    {
                        var source = sourceMeshes[part.objectName];
                        var node = CopyTransform(source.transform, model.transform, root.transform, copied);
                        var filter = node.gameObject.AddComponent<MeshFilter>();
                        filter.sharedMesh = source.sharedMesh;
                        var renderer = node.gameObject.AddComponent<MeshRenderer>();
                        var sourceRenderer = source.GetComponent<Renderer>();
                        renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                        renderer.shadowCastingMode = sourceRenderer.shadowCastingMode;
                        renderer.receiveShadows = sourceRenderer.receiveShadows;
                        selectedMeshes.Add(part.objectName, filter);
                    }
                    copied[model.transform].name = system.id;
                    meshes[system.id] = selectedMeshes;
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
                    if (!addCatalogColliders) continue;
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
                var geometry = root.GetComponentsInChildren<MeshFilter>(true);
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                long triangles = geometry.Sum(filter => (long)filter.sharedMesh.triangles.Length / 3);
                if (prefabPath == OrganOverviewPrefabPath && (geometry.Length != OrganOverviewPartCount
                    || triangles != OrganOverviewExpectedTriangles || triangles > OrganOverviewTriangleBudget || colliders != 0))
                    throw new InvalidOperationException("Imported organ overview does not match its bounded render-only geometry contract.");
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
                if (!success || prefab == null) throw new InvalidOperationException("Unity could not save " + prefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[Scalpal anatomy] Saved " + prefabPath + ": " + atlas.parts.Length + " parts, "
                    + atlas.systems.Length + " systems, " + colliders + " catalog colliders, " + triangles + " triangles; "
                    + "source-frame bounds center=" + bounds.center.ToString("F4") + " size=" + bounds.size.ToString("F4") + ". "
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

        static Transform CopyTransform(Transform source, Transform modelRoot, Transform outputRoot,
            Dictionary<Transform, Transform> copied)
        {
            if (copied.TryGetValue(source, out var existing)) return existing;
            var parent = source == modelRoot ? outputRoot : CopyTransform(source.parent, modelRoot, outputRoot, copied);
            var node = new GameObject(source.name).transform;
            node.SetParent(parent, false);
            node.localPosition = source.localPosition;
            node.localRotation = source.localRotation;
            node.localScale = source.localScale;
            copied.Add(source, node);
            return node;
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
            bool standard = shader.name == "Standard";
            material.SetColor(standard ? "_Color" : "_BaseColor", color);
            material.SetFloat(standard ? "_Glossiness" : "_Smoothness", 0.3f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat(standard ? "_Mode" : "_Surface", ghost ? (standard ? 2f : 1f) : 0f);
            material.SetFloat("_ZWrite", ghost ? 0f : 1f);
            material.SetFloat("_SrcBlend", ghost ? (float)UnityEngine.Rendering.BlendMode.SrcAlpha : (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", ghost ? (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (float)UnityEngine.Rendering.BlendMode.Zero);
            material.SetOverrideTag("RenderType", ghost ? "Transparent" : "Opaque");
            material.SetShaderPassEnabled("ShadowCaster", !ghost);
            material.SetColor("_EmissionColor", Color.black);
            material.EnableKeyword("_EMISSION");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (ghost) material.EnableKeyword(standard ? "_ALPHABLEND_ON" : "_SURFACE_TYPE_TRANSPARENT");
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
