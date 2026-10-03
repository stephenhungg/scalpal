using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Instruments.Editor
{
    public static class InstrumentAssetBuilder
    {
        const string Root = "Assets/Scalpal/Instruments";
        public static readonly string[] ToolIds = {
            "trocar_5mm", "trocar_12mm", "laparoscope_30", "atraumatic_grasper", "maryland_dissector", "hook_cautery", "vessel_sealer", "clip_applier",
            "lap_scissors", "endo_stapler", "circular_stapler", "suction_irrigator", "retrieval_bag", "fascial_closure", "scalpel"
        };

        [MenuItem("Scalpal/Instruments/Build Prefabs and Sandbox")]
        static void BuildFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BuildAll();
        }

        public static void BuildAll()
        {
            Directory.CreateDirectory(Root + "/Prefabs");
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/Samples");
            AssetDatabase.Refresh();
            foreach (string id in ToolIds) BuildInstrument(id);
            BuildPatch();
            BuildSandbox();
            AssetDatabase.SaveAssets();
            Debug.Log("SCALPAL_INSTRUMENT_BUILD_OK: 15 instruments, teaching patch, sandbox. Headset behavior is untested.");
        }

        static InstrumentAction ActionFor(string id)
        {
            switch (id)
            {
                case "scalpel": case "lap_scissors": return InstrumentAction.Cut;
                case "atraumatic_grasper": case "maryland_dissector": return InstrumentAction.Grasp;
                case "hook_cautery": case "vessel_sealer": return InstrumentAction.Seal;
                case "clip_applier": return InstrumentAction.Clip;
                case "endo_stapler": case "circular_stapler": return InstrumentAction.Staple;
                case "suction_irrigator": return InstrumentAction.Suction;
                case "retrieval_bag": return InstrumentAction.Retrieve;
                case "fascial_closure": return InstrumentAction.Close;
                case "trocar_5mm": case "trocar_12mm": return InstrumentAction.PlacePort;
                default: return InstrumentAction.None;
            }
        }

        static Transform Find(Transform root, string name) => root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);

        static void BuildInstrument(string id)
        {
            string path = Root + "/Models/inst_" + id + ".fbx";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null) throw new InvalidOperationException("Missing instrument model: " + path);
            var root = new GameObject("inst_" + id);
            var geometry = (GameObject)PrefabUtility.InstantiatePrefab(model);
            geometry.name = "Geometry";
            geometry.transform.SetParent(root.transform, false);
            var grip = Find(geometry.transform, "GripAnchor");
            var tip = Find(geometry.transform, "ActionPoint") ?? Find(geometry.transform, "Tip");
            if (grip == null || tip == null) { UnityEngine.Object.DestroyImmediate(root); throw new InvalidOperationException(id + " requires GripAnchor and Tip/ActionPoint"); }
            // FBX importer axis conversion is normalized from named anchors, not guessed Euler angles.
            Vector3 distal = tip.position - grip.position;
            if (distal.magnitude < 0.03f || distal.magnitude > 1f) { UnityEngine.Object.DestroyImmediate(root); throw new InvalidOperationException(id + " invalid metric anchor distance: " + distal.magnitude); }
            geometry.transform.rotation = Quaternion.FromToRotation(distal.normalized, Vector3.forward) * geometry.transform.rotation;
            geometry.transform.position -= grip.position;
            grip.rotation = root.transform.rotation;
            var body = root.AddComponent<Rigidbody>();
            body.mass = 0.18f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            var handle = root.AddComponent<BoxCollider>();
            handle.center = root.transform.InverseTransformPoint(grip.position);
            handle.size = new Vector3(0.055f, 0.08f, 0.045f);
            var shaft = root.AddComponent<CapsuleCollider>();
            Vector3 tipLocal = root.transform.InverseTransformPoint(tip.position);
            shaft.direction = 2;
            shaft.center = tipLocal * 0.5f;
            shaft.radius = id == "trocar_12mm" || id.Contains("stapler") ? 0.01f : 0.006f;
            shaft.height = tipLocal.magnitude;
            var tool = root.AddComponent<InstrumentBehaviour>();
            tool.instrumentId = id;
            tool.action = ActionFor(id);
            tool.gripAnchor = grip;
            tool.actionPoint = tip;
            tool.upperJaw = Find(geometry.transform, "JawUpper");
            tool.lowerJaw = Find(geometry.transform, "JawLower");
            tool.triggerPart = Find(geometry.transform, "Trigger");
            tool.triggerSlides = id == "suction_irrigator" || id == "fascial_closure";
            tool.triggerSlide = id == "suction_irrigator" ? Vector3.down * 0.006f : Vector3.forward * 0.008f;
            tool.deployedBag = Find(geometry.transform, "Bag");
            tool.foldedBag = Find(geometry.transform, "BagFolded");
            tool.anvil = id == "circular_stapler" ? Find(geometry.transform, "Anvil") : null;
            tool.obturator = Find(geometry.transform, "Obturator");
            if (id == "laparoscope_30")
            {
                var optic = Find(geometry.transform, "OpticDirection");
                if (optic != null) optic.rotation = root.transform.rotation * Quaternion.Euler(30, 0, 0);
                root.AddComponent<LaparoscopeView>().opticDirection = optic;
            }
            if (tool.deployedBag != null) tool.deployedBag.gameObject.SetActive(false);
            if (tool.foldedBag != null) tool.foldedBag.gameObject.SetActive(true);
            var tipObject = Find(geometry.transform, "Tip");
            if (tipObject == null) throw new InvalidOperationException(id + " requires an explicitly named Tip");
            var tipTrigger = tipObject.gameObject.AddComponent<SphereCollider>();
            tipTrigger.isTrigger = true;
            tipTrigger.radius = tool.contactRadius;
            tipObject.gameObject.AddComponent<InstrumentTipContact>();
            root.AddComponent<InstrumentProjection>();
            int instrumentLayer = LayerMask.NameToLayer("Instrument");
            if (instrumentLayer >= 0) foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = instrumentLayer;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    string label = materials[i] != null ? materials[i].name.ToLowerInvariant() : renderer.name.ToLowerInvariant();
                    materials[i] = ImportedMaterial(label, materials[i]);
                }
                renderer.sharedMaterials = materials;
            }
            PrefabUtility.SaveAsPrefabAsset(root, Root + "/Prefabs/inst_" + id + ".prefab");
            UnityEngine.Object.DestroyImmediate(root);
        }

        static Material ImportedMaterial(string label, Material original)
        {
            // Preserve imported diffuse accents; FBX drops Principled metallic/roughness, restore from the original named material recipe.
            Color color = original != null && original.HasProperty("_Color") ? original.color : new Color(0.55f, 0.62f, 0.67f);
            float metal = 0.18f, smooth = 0.7f;
            if (label.Contains("polishededge")) { metal = 1; smooth = 0.87f; }
            else if (label.Contains("steel")) { metal = 0.92f; smooth = 0.77f; }
            else if (label.Contains("blackpolymer")) { metal = 0.05f; smooth = 0.71f; }
            else if (label.Contains("griprubber")) { metal = 0; smooth = 0.42f; }
            else if (label.Contains("ceramic")) { metal = 0.05f; smooth = 0.72f; }
            else if (label.Contains("opticalglass")) { metal = 0.65f; smooth = 0.91f; }
            else if (label.Contains("retrievalmembrane")) { metal = 0; smooth = 0.55f; }
            string safeName = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            return Material(safeName, color, metal, smooth);
        }

        static Material Material(string name, Color color, float metal = 0, float smooth = 0.3f)
        {
            string path = Root + "/Materials/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("No supported PBR shader installed");
            var mat = existing != null ? existing : new Material(shader);
            mat.shader = shader;
            mat.name = name;
            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metal);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smooth);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smooth);
            if (existing == null) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        static GameObject Cube(string name, Transform parent, Vector3 position, Vector3 size, Material material, bool collider = true)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position;
            cube.transform.localScale = size;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            if (!collider) UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
            return cube;
        }

        static void BuildPatch()
        {
            var root = new GameObject("TrainingPatch");
            var target = root.AddComponent<TrainingTarget>();
            target.leftHalf = Cube("PatchHalfA", root.transform, new Vector3(0, 0, -0.02f), new Vector3(0.12f, 0.012f, 0.04f), Material("TeachingTissue", new Color(0.7f, 0.19f, 0.26f)), false).transform;
            target.rightHalf = Cube("PatchHalfB", root.transform, new Vector3(0, 0, 0.02f), new Vector3(0.12f, 0.012f, 0.04f), Material("TeachingTissue", new Color(0.7f, 0.19f, 0.26f)), false).transform;
            target.fluidVisual = Cube("FluidDemo", root.transform, new Vector3(0, -0.02f, 0), new Vector3(0.1f, 0.02f, 0.07f), Material("DemoFluid", new Color(0.4f, 0.01f, 0.03f)), false).transform;
            var contact = root.AddComponent<BoxCollider>();
            contact.isTrigger = true;
            contact.size = new Vector3(0.12f, 0.025f, 0.08f);
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            target.allowedActions = TrainingActions.Cut | TrainingActions.Grasp | TrainingActions.Seal | TrainingActions.Clip | TrainingActions.Staple | TrainingActions.Suction | TrainingActions.Retrieve | TrainingActions.PlacePort | TrainingActions.Close;
            // Registration remains invalid in this reusable prefab. Sandbox explicitly enables it.
            PrefabUtility.SaveAsPrefabAsset(root, Root + "/Prefabs/TrainingPatch.prefab");
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void BuildSandbox()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("DesktopPreviewCamera").AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0, 1.7f, -1), Quaternion.Euler(30, 0, 0));
            camera.gameObject.tag = "MainCamera";
            camera.nearClipPlane = 0.01f;
            var light = new GameObject("WorkbenchLight").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(45, -30, 0);
            light.intensity = 1.3f;
            Cube("Workbench", null, new Vector3(0, 0.7f, 0.35f), new Vector3(1.2f, 0.05f, 1.3f), Material("Workbench", new Color(0.08f, 0.12f, 0.13f)));
            var scopeMonitor = GameObject.CreatePrimitive(PrimitiveType.Quad);
            scopeMonitor.name = "VirtualLaparoscopeMonitor";
            scopeMonitor.transform.SetPositionAndRotation(new Vector3(0.63f, 1.13f, 0.45f), Quaternion.Euler(0, -15, 0));
            scopeMonitor.transform.localScale = new Vector3(0.35f, 0.35f, 1);
            UnityEngine.Object.DestroyImmediate(scopeMonitor.GetComponent<Collider>());
            scopeMonitor.GetComponent<Renderer>().sharedMaterial = Material("MonitorIdle", Color.black);
            for (int i = 0; i < ToolIds.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/inst_" + ToolIds[i] + ".prefab");
                var tool = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                tool.transform.SetPositionAndRotation(new Vector3(-0.5f + (i % 5) * 0.25f, 0.81f, -0.05f + (i / 5) * 0.4f), Quaternion.Euler(0, 0, 90));
                var scopeView = tool.GetComponent<LaparoscopeView>();
                if (scopeView != null) scopeView.monitor = scopeMonitor.GetComponent<Renderer>();
            }
            var patch = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/TrainingPatch.prefab"));
            patch.transform.position = new Vector3(0, 0.95f, 0.1f);
            patch.GetComponent<TrainingTarget>().SetRegistrationValid(true);
            var origin = new GameObject("TrackingOrigin").transform;
            foreach (var node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                var hand = new GameObject(node.ToString());
                hand.transform.SetParent(origin, false);
                hand.AddComponent<InstrumentInteractor>();
                var xr = hand.AddComponent<XRInstrumentInput>();
                xr.controller = node;
                xr.trackingOrigin = origin;
            }
            EditorSceneManager.SaveScene(scene, Root + "/Samples/InstrumentSandbox.unity");
            if (previous.Any(s => !string.IsNullOrEmpty(s.path))) EditorSceneManager.RestoreSceneManagerSetup(previous);
        }
    }
}
