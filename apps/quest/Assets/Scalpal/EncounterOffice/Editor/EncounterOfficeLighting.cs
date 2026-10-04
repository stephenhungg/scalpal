using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Scalpal.EncounterOffice.Editor
{
    /// <summary>
    /// Fully baked clinic lighting for Quest: lightmaps + light probes + one baked reflection probe, zero realtime lights.
    /// Positions come from the authored Blender room (Unity = -bx, bz, -by after the measured yaw) and are checked against the imported glazing.
    /// </summary>
    public static class EncounterOfficeLighting
    {
        public const string LightingRoot = EncounterOfficeBuild.Root + "/Art/Lighting";
        public const string SettingsPath = LightingRoot + "/DiagnosisOfficeLighting.lighting";
        public const string CookiePath = LightingRoot + "/WindowBlindsCookie.png";
        public const string CeilingPath = LightingRoot + "/OfficeCeiling.asset";
        public const string OfficeModelPath = EncounterOfficeBuild.Root + "/Art/Models/DoctorOffice.fbx";
        public const int LightmapTexelsPerMetre = 40, MaxAtlasSize = 1024;
        // Small, many-chart decorations are probe lit: unwrapping thousands of petals wastes atlas space and seams badly.
        public static readonly string[] ProbeLitOfficeMaterials = { "Office_FlowerRose", "Office_FlowerLilac", "Office_FlowerButter", "Office_Plant", "Office_LampWarm", "Office_WindowSky" };
        static readonly Vector3 WindowCentre = new Vector3(-1.62f, 1.87f, -2.335f);
        // Interior faces of the authored 5.8 x 6.0 x 3.0 m room.
        const float RoomMinX = -2.84f, RoomMaxX = 2.84f, RoomMinZ = -2.44f, RoomMaxZ = 3.39f, CeilingY = 2.96f;

        public static void Apply(GameObject office, params GameObject[] patients)
        {
            if (!LightmapUVsConfigured()) throw new InvalidOperationException("Call EnsureLightmapUVs before instantiating the office.");
            var glazing = office.GetComponentsInChildren<Renderer>(true).SingleOrDefault(renderer => renderer.name == "Body_Office_WindowSky");
            if (!glazing || Vector3.Distance(glazing.bounds.center, WindowCentre) > .05f)
                throw new InvalidOperationException("Office window moved; baked window lights must be re-placed. Measured " + (glazing ? glazing.bounds.center.ToString("F3") : "missing"));

            var ceiling = new GameObject("BakedCeiling", typeof(MeshFilter), typeof(MeshRenderer));
            ceiling.transform.SetParent(office.transform, true);
            ceiling.GetComponent<MeshFilter>().sharedMesh = CeilingMesh();
            ceiling.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(EncounterOfficeBuild.Root + "/Materials/art_Office_WallWarm.mat");

            foreach (var renderer in office.GetComponentsInChildren<MeshRenderer>(true))
            {
                string material = renderer.name.Replace("Body_", "");
                bool probeLit = ProbeLitOfficeMaterials.Contains(material);
                GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic);
                renderer.receiveGI = probeLit ? ReceiveGI.LightProbes : ReceiveGI.Lightmaps;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
                // Static casters only feed the bake: no realtime shadow-casting light exists at runtime.
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.scaleInLightmap = renderer.gameObject == ceiling ? .5f : 1f;
            }
            foreach (var patient in patients)
            {
                var head = patient.GetComponentsInChildren<Transform>(true).Single(t => t.name == "HeadPivot");
                foreach (var renderer in patient.GetComponentsInChildren<Renderer>(true))
                {
                    GameObjectUtility.SetStaticEditorFlags(renderer.gameObject, 0);
                    renderer.lightProbeUsage = LightProbeUsage.BlendProbes; renderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                    // One anchor per person so hair, face and clothing share one interpolated probe instead of six.
                    renderer.probeAnchor = head;
                }
            }
            ShadeMaterials();

            var rig = new GameObject("BakedClinicLighting").transform;
            var sun = Light(rig, "WindowSunThroughBlinds", LightType.Spot, new Color(1f, .88f, .72f), 2.6f, WindowCentre + new Vector3(0, .45f, .28f));
            sun.transform.rotation = Quaternion.LookRotation(new Vector3(-.35f, 0, -.15f) - sun.transform.position, Vector3.up);
            sun.spotAngle = 78; sun.innerSpotAngle = 46; sun.range = 7.5f; sun.shadowRadius = .06f; sun.cookie = BlindsCookie();
            var sky = Light(rig, "WindowSkyFill", LightType.Rectangle, new Color(.82f, .90f, 1f), 2.6f, WindowCentre + new Vector3(0, 0, .095f));
            sky.transform.rotation = Quaternion.LookRotation(Vector3.forward); sky.areaSize = new Vector2(1.5f, 1.15f); sky.range = 8;
            foreach (float x in new[] { -1.65f, 1.65f })
            {
                var panel = Light(rig, "CeilingDiffuser", LightType.Rectangle, new Color(1f, .95f, .88f), 2.4f, new Vector3(x, 2.87f, -.15f));
                panel.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward); panel.areaSize = new Vector2(.97f, .38f); panel.range = 8;
            }
            var lamp = Light(rig, "FloorLampWarm", LightType.Point, new Color(1f, .70f, .44f), 1.5f, new Vector3(1.08f, 1.47f, -1.25f));
            lamp.range = 3.2f; lamp.shadowRadius = .12f;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(.80f, .85f, .88f);
            RenderSettings.ambientEquatorColor = new Color(.82f, .76f, .72f);
            RenderSettings.ambientGroundColor = new Color(.46f, .41f, .37f);
            RenderSettings.ambientIntensity = 1;
            RenderSettings.defaultReflectionResolution = 128;

            var probe = new GameObject("ClinicReflectionProbe").AddComponent<ReflectionProbe>();
            probe.transform.SetParent(rig, false);
            probe.transform.position = new Vector3(0, 1.25f, .45f);
            probe.mode = ReflectionProbeMode.Baked; probe.boxProjection = true; probe.resolution = 128; probe.hdr = true;
            probe.center = new Vector3((RoomMinX + RoomMaxX) / 2, CeilingY / 2, (RoomMinZ + RoomMaxZ) / 2) - probe.transform.position;
            probe.size = new Vector3(RoomMaxX - RoomMinX, CeilingY, RoomMaxZ - RoomMinZ);
            probe.importance = 1; probe.intensity = 1;

            var group = new GameObject("PatientLightProbes").AddComponent<LightProbeGroup>();
            group.transform.SetParent(rig, false);
            group.probePositions = ProbePositions(office, patients).ToArray();

            Lightmapping.lightingSettings = Settings();
            Debug.Log("SCALPAL_ENCOUNTER_LIGHTING_APPLIED probes=" + group.probePositions.Length + " lights=" + rig.GetComponentsInChildren<Light>().Length);
        }

        /// <summary>Bakes lightmaps, probes and reflection probe next to the saved scene. Needs a graphics device for the reflection cubemap.</summary>
        public static void Bake(Scene scene)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Office lighting bake needs a graphics device for the reflection probe; run Prepare without -nographics.");
            var started = DateTime.UtcNow;
            if (!Lightmapping.Bake()) throw new InvalidOperationException("Office lightmap bake failed or was cancelled.");
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            var maps = LightmapSettings.lightmaps;
            Debug.Log("SCALPAL_ENCOUNTER_LIGHTING_BAKED seconds=" + (int)(DateTime.UtcNow - started).TotalSeconds + " lightmapper=" + Lightmapping.lightingSettings.lightmapper
                + " atlases=" + string.Join(",", maps.Select(map => map.lightmapColor ? map.lightmapColor.width + "x" + map.lightmapColor.height : "missing"))
                + " probes=" + (LightmapSettings.lightProbes ? LightmapSettings.lightProbes.count : 0));
        }

        static bool LightmapUVsConfigured()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(OfficeModelPath);
            return !importer.generateSecondaryUV && AssetDatabase.LoadAssetAtPath<GameObject>(OfficeModelPath).GetComponentsInChildren<MeshFilter>(true)
                .All(filter => filter.sharedMesh && filter.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord1));
        }

        /// <summary>
        /// The office FBX carries Blender-authored lightmap UVs (scripts/environments/doctor_office.py, second UV set).
        /// Unity's import unwrapper is kept off: it collapsed the bevelled window frame into a black sliver.
        /// </summary>
        public static void EnsureLightmapUVs()
        {
            Directory.CreateDirectory(LightingRoot); AssetDatabase.Refresh();
            var importer = (ModelImporter)AssetImporter.GetAtPath(OfficeModelPath);
            if (importer.generateSecondaryUV) { importer.generateSecondaryUV = false; importer.SaveAndReimport(); }
            if (!LightmapUVsConfigured())
                throw new InvalidOperationException("DoctorOffice.fbx lacks authored lightmap UVs; re-export it with scripts/environments/doctor_office.py.");
        }

        static LightingSettings Settings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(SettingsPath);
            if (!settings) { settings = new LightingSettings { name = "DiagnosisOfficeLighting" }; AssetDatabase.CreateAsset(settings, SettingsPath); }
            settings.bakedGI = true; settings.realtimeGI = false; settings.autoGenerate = false;
            settings.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            settings.mixedBakeMode = MixedLightingMode.IndirectOnly;
            settings.directionalityMode = LightmapsMode.NonDirectional;
            settings.lightmapResolution = LightmapTexelsPerMetre; settings.lightmapPadding = 4; settings.lightmapMaxSize = MaxAtlasSize;
            settings.lightmapCompression = LightmapCompression.HighQuality;
            settings.ao = true; settings.aoMaxDistance = .55f; settings.aoExponentIndirect = 1.15f; settings.aoExponentDirect = .25f;
            settings.directSampleCount = 64; settings.indirectSampleCount = 512; settings.environmentSampleCount = 256; settings.maxBounces = 4;
            settings.filteringMode = LightingSettings.FilterMode.Auto;
            settings.indirectScale = 1.3f; settings.albedoBoost = 1.25f;
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssets();
            return settings;
        }

        static Light Light(Transform parent, string name, LightType type, Color color, float intensity, Vector3 position)
        {
            var light = new GameObject(name).AddComponent<Light>();
            light.transform.SetParent(parent, false); light.transform.position = position;
            light.type = type; light.color = color; light.intensity = intensity; light.bounceIntensity = 1;
            light.lightmapBakeType = LightmapBakeType.Baked; light.shadows = LightShadows.Soft;
            return light;
        }

        static void ShadeMaterials()
        {
            // Palette albedo stays authored; only surface response and emissive practicals change.
            Material Art(string name) => AssetDatabase.LoadAssetAtPath<Material>(EncounterOfficeBuild.Root + "/Materials/art_" + name + ".mat")
                ?? throw new InvalidOperationException("Office art material missing: " + name);
            void Emit(Material material, Color emission)
            {
                material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", emission);
                // Baked area/point lights carry the actual light; emission only makes the source read as bright.
                // None (not EmissiveIsBlack): Standard's material validation strips _EMISSION from EmissiveIsBlack materials.
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None; EditorUtility.SetDirty(material);
            }
            Emit(Art("Office_LampWarm"), new Color(1f, .82f, .58f) * 1.25f);
            Emit(Art("Office_WindowSky"), new Color(.86f, .95f, 1f) * 1.05f);
            var floor = Art("Office_Floor"); floor.SetFloat("_Glossiness", .48f); EditorUtility.SetDirty(floor);
            var metal = Art("Office_Metal"); metal.SetFloat("_Glossiness", .72f); EditorUtility.SetDirty(metal);
            AssetDatabase.SaveAssets();
        }

        static Texture2D BlindsCookie()
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = (x + .5f) / size * 2 - 1, v = (y + .5f) / size * 2 - 1;
                    // Window pane silhouette with soft frame, centre mullion and nine soft horizontal slat shadows.
                    float pane = Smooth(.92f, .74f, Mathf.Abs(u)) * Smooth(.92f, .70f, Mathf.Abs(v));
                    float mullion = Mathf.Lerp(.55f, 1, Smooth(.015f, .05f, Mathf.Abs(u)));
                    float slat = Mathf.Lerp(.42f, 1, Smooth(.07f, .2f, Mathf.Abs(Mathf.Repeat(v * 4.5f, 1) - .5f)));
                    byte value = (byte)Mathf.RoundToInt(Mathf.Clamp01(pane * mullion * slat) * 255);
                    pixels[y * size + x] = new Color32(value, value, value, value);
                }
            texture.SetPixels32(pixels);
            File.WriteAllBytes(CookiePath, texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(CookiePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(CookiePath);
            importer.textureType = TextureImporterType.Cookie; importer.textureShape = TextureImporterShape.Texture2D;
            importer.alphaSource = TextureImporterAlphaSource.FromGrayScale; importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true; importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(CookiePath);
        }
        static float Smooth(float edge0, float edge1, float value) => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(edge0, edge1, value));

        static Mesh CeilingMesh()
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(CeilingPath);
            bool create = !mesh; if (create) mesh = new Mesh { name = "OfficeCeiling" };
            // Overlaps the wall tops by 2cm so the bake cannot leak sky light through the seam.
            float x0 = RoomMinX - .02f, x1 = RoomMaxX + .02f, z0 = RoomMinZ - .02f, z1 = RoomMaxZ + .02f;
            mesh.Clear();
            mesh.vertices = new[] { new Vector3(x0, CeilingY, z0), new Vector3(x1, CeilingY, z0), new Vector3(x1, CeilingY, z1), new Vector3(x0, CeilingY, z1) };
            var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.uv = uv; mesh.uv2 = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; // faces down into the room
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            if (create) AssetDatabase.CreateAsset(mesh, CeilingPath); else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        static IEnumerable<Vector3> ProbePositions(GameObject office, GameObject[] patients)
        {
            var candidates = new List<Vector3>();
            for (int ix = 0; ix < 7; ix++) for (int iz = 0; iz < 7; iz++) foreach (float y in new[] { .2f, .95f, 1.7f, 2.55f })
                candidates.Add(new Vector3(Mathf.Lerp(RoomMinX + .3f, RoomMaxX - .3f, ix / 6f), y, Mathf.Lerp(RoomMinZ + .3f, RoomMaxZ - .3f, iz / 6f)));
            // Dense cage around the seated patient(s) so the head/torso see the window key and lamp gradient.
            // Both templates share one seat; centre a single cage on their mean head position.
            var heads = patients.Select(p => p.GetComponentsInChildren<Transform>(true).Single(t => t.name == "HeadPivot").position).ToArray();
            var seat = new Vector3(heads.Average(h => h.x), 0, heads.Average(h => h.z));
            for (int ix = -1; ix <= 1; ix++) for (int iz = -1; iz <= 1; iz++) foreach (float y in new[] { .45f, .9f, 1.3f, 1.7f, 2.1f })
                candidates.Add(seat + new Vector3(ix * .45f, y, iz * .45f));
            // Reject probes buried in furniture: they would bake black and darken whoever interpolates them.
            var colliders = new List<MeshCollider>();
            bool backfaces = Physics.queriesHitBackfaces;
            try
            {
                foreach (var filter in office.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh) { var collider = filter.gameObject.AddComponent<MeshCollider>(); collider.sharedMesh = filter.sharedMesh; colliders.Add(collider); }
                Physics.SyncTransforms(); Physics.queriesHitBackfaces = true;
                var directions = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
                foreach (var point in candidates)
                {
                    if (Physics.CheckSphere(point, .06f)) continue;
                    int inside = directions.Count(direction => Physics.Raycast(point, direction, out var hit, 12) && Vector3.Dot(hit.normal, direction) > 0);
                    if (inside < 2) yield return point;
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                foreach (var collider in colliders) UnityEngine.Object.DestroyImmediate(collider);
            }
        }

        [MenuItem("Scalpal/Encounter Office/Capture Lighting Preview")]
        public static void CapturePreview()
        {
            // Seated learner view with the female patient shown; SCALPAL_ENCOUNTER_PREVIEW is an absolute PNG path.
            string output = Environment.GetEnvironmentVariable("SCALPAL_ENCOUNTER_PREVIEW");
            if (string.IsNullOrWhiteSpace(output) || !Path.IsPathRooted(output)) throw new InvalidOperationException("SCALPAL_ENCOUNTER_PREVIEW must be an absolute output path.");
            EditorSceneManager.OpenScene(EncounterOfficeBuild.ScenePath, OpenSceneMode.Single);
            var female = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None).First(t => t.name == "GenericAdultFemaleTemplate").gameObject;
            female.SetActive(true);
            // SCALPAL_ENCOUNTER_PREVIEW_UI=hidden isolates the room lighting from the frosted UI cards.
            if (Environment.GetEnvironmentVariable("SCALPAL_ENCOUNTER_PREVIEW_UI") == "hidden")
                UnityEngine.Object.FindFirstObjectByType<EncounterOfficePanel>().gameObject.SetActive(false);
            var camera = UnityEngine.Object.FindFirstObjectByType<EncounterOfficeRig>().head;
            var render = new RenderTexture(1920, 1080, 24) { antiAliasing = 4 };
            var previous = RenderTexture.active;
            var texture = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            try
            {
                camera.stereoTargetEye = StereoTargetEyeMask.None; camera.fieldOfView = 70; camera.targetTexture = render; camera.Render();
                RenderTexture.active = render; texture.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); texture.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllBytes(output, texture.EncodeToPNG());
                Debug.Log("SCALPAL_ENCOUNTER_LIGHTING_PREVIEW_OK path=" + output);
            }
            finally { camera.targetTexture = null; RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(render); }
        }
    }
}
