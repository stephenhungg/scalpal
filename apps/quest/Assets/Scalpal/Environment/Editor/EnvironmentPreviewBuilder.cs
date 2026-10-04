using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Environments.Editor
{
    // Static art preview only. This does not initialize XR, registration or exercise state.
    public static class EnvironmentPreviewBuilder
    {
        const string Root = "Assets/Scalpal/Environment/";
        const string ScenePath = Root + "Samples/OperatingRoomPreview.unity";

        [MenuItem("Scalpal/Environment/Build Static Operating Room Preview")]
        public static void Build()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string directory in new[] { "Materials", "Prefabs", "Samples" }) Directory.CreateDirectory(Root + directory);
                AssetDatabase.Refresh();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var room = CreateModel("OperatingTheatre");
                room.name = "VirtualOperatingRoom";
                AddRoomCollision(room);
                PrefabUtility.SaveAsPrefabAsset(room, Root + "Prefabs/OperatingTheatre.prefab");
                UnityEngine.Object.DestroyImmediate(room);
                room = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/OperatingTheatre.prefab"));
                var patient = CreateModel("SupinePatient");
                patient.name = "VirtualPatient";
                AddPatientCollision(patient);
                PrefabUtility.SaveAsPrefabAsset(patient, Root + "Prefabs/SupinePatient.prefab");
                UnityEngine.Object.DestroyImmediate(patient);
                patient = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/SupinePatient.prefab"));
                var patientRoot = new GameObject("PatientRoot");
                patient.transform.SetParent(patientRoot.transform, true);
                var bounds = GetBounds(patient);
                patient.transform.position += new Vector3(0, 0.98f - bounds.min.y, 0);
                new GameObject("AnatomyRoot_Unbound").transform.SetParent(patientRoot.transform);
                var camera = new GameObject("PreviewCamera").AddComponent<Camera>();
                camera.transform.position = new Vector3(-2.6f, 2.4f, -3.0f);
                camera.transform.LookAt(new Vector3(0, 1.1f, 0));
                camera.fieldOfView = 60; camera.nearClipPlane = 0.05f; camera.farClipPlane = 30;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.12f, 0.15f, 0.19f);
                var light = new GameObject("PreviewLight").AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.2f;
                light.transform.rotation = Quaternion.Euler(45, -25, 0);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.6f, 0.65f, 0.7f);
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), ScenePath);
                AssetDatabase.SaveAssets();
                Debug.Log("SCALPAL_ENVIRONMENT_PREVIEW_BUILT: static room and reclining patient; no XR or surgery behavior.");
                Validate();
            }
            finally { if (previous.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previous); }
        }

        public const string RoomCollisionName = "RoomCollision", PatientCollisionName = "PatientCollision", InstrumentStandName = "InstrumentStand";
        // Static physical stand-ins for the combined room/patient art, so a dropped tool rests on the operating
        // table, the instrument stands, the floor and the body instead of falling through them. Ignore Raycast keeps them out of the scene identity
        // pointer, which labels anatomy and instruments only; rigidbodies still collide with that layer.
        static void AddRoomCollision(GameObject room)
        {
            var table = CollisionChild(room.transform, RoomCollisionName);
            // Prefab-local metres, measured by raycasting the table meshes: the steel frame (top 0.97 m), the main
            // mattress (top 1.07 m), the head pad (top 1.06 m), the foot pad sloping 1.07 -> 1.03 m, and the pedestal.
            Box(table, new Vector3(0, .92f, 0), new Vector3(.6f, .1f, 1.94f), Vector3.zero);
            Box(table, new Vector3(0, 1.02f, .025f), new Vector3(.5f, .1f, 1.15f), Vector3.zero);
            Box(table, new Vector3(0, 1.01f, .825f), new Vector3(.5f, .1f, .3f), Vector3.zero);
            Box(table, new Vector3(0, .999f, -.75f), new Vector3(.5f, .1f, .4f), new Vector3(8.3f, 0, 0));
            Box(table, new Vector3(0, .782f, 0), new Vector3(.34f, .304f, .4f), Vector3.zero);
            // The round instrument stand (top 1.02 m), the angled instrument tray (top 1.05 m) and the floor (top 0.06 m).
            Box(table, new Vector3(-.575f, .97f, .4f), new Vector3(.34f, .1f, .46f), Vector3.zero, InstrumentStandName);
            Box(table, new Vector3(-1.065f, 1f, .945f), new Vector3(.55f, .1f, .34f), new Vector3(0, 36f, 0));
            Box(table, new Vector3(0, .01f, 0), new Vector3(8f, .1f, 8f), Vector3.zero);
        }
        static void Box(GameObject table, Vector3 center, Vector3 size, Vector3 euler, string name = "Box")
        {
            var piece = new GameObject(name) { layer = table.layer }; piece.transform.SetParent(table.transform, false);
            piece.transform.SetLocalPositionAndRotation(center, Quaternion.Euler(euler));
            piece.AddComponent<BoxCollider>().size = size;
        }
        // A collision copy of the body mesh with every triangle wound outward. Part of the source midline is wound
        // inward, and a static triangle mesh only stops a tool from its front side, so dropped tools fell into the
        // chest. Outward is judged from the axis of the limb or trunk a triangle belongs to (authored bounds around
        // the model pivot, head toward local -Z), so the inner thighs and the sides facing the arms stay correct.
        const string PatientCollisionMesh = Root + "Models/SupinePatientCollision.asset";
        static Vector2 PartAxis(Vector3 p)
        {
            float x = Mathf.Abs(p.x);
            if (p.z >= -.53f && x >= .175f) return new Vector2(Mathf.Sign(p.x) * .21f, -.03f); // arm
            if (p.z >= .25f) return new Vector2(Mathf.Sign(p.x) * .085f, -.03f);              // leg
            return new Vector2(0, -.03f);                                                     // head and trunk
        }
        static void AddPatientCollision(GameObject patient)
        {
            var source = patient.GetComponent<MeshFilter>().sharedMesh;
            if (!source) throw new InvalidOperationException("Patient mesh missing.");
            var vertices = source.vertices; var triangles = source.triangles;
            var toBody = Matrix4x4.TRS(Vector3.zero, patient.transform.rotation, patient.transform.lossyScale);
            int flipped = 0;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = toBody.MultiplyPoint3x4(vertices[triangles[i]]), b = toBody.MultiplyPoint3x4(vertices[triangles[i + 1]]), c = toBody.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                Vector3 centroid = (a + b + c) / 3; var axis = PartAxis(centroid);
                Vector3 outward = new Vector3(centroid.x - axis.x, centroid.y - axis.y, 0);
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) { (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]); flipped++; }
            }
            var mesh = new Mesh { name = "SupinePatientCollision", vertices = vertices, triangles = triangles };
            mesh.RecalculateBounds();
            AssetDatabase.DeleteAsset(PatientCollisionMesh);
            AssetDatabase.CreateAsset(mesh, PatientCollisionMesh);
            AssetDatabase.SaveAssets();
            CollisionChild(patient.transform, PatientCollisionName).AddComponent<MeshCollider>().sharedMesh = mesh;
            Debug.Log("SCALPAL_PATIENT_COLLISION triangles=" + triangles.Length / 3 + " reoriented=" + flipped);
        }
        static GameObject CollisionChild(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing) UnityEngine.Object.DestroyImmediate(existing.gameObject);
            var child = new GameObject(name) { layer = 2 };
            child.transform.SetParent(parent, false);
            return child;
        }
        // Adds the collision children to the committed prefabs without rebuilding their art.
        public static void ApplyCollision()
        {
            foreach (var (name, add) in new (string, Action<GameObject>)[] { ("OperatingTheatre", AddRoomCollision), ("SupinePatient", AddPatientCollision) })
            {
                string path = Root + "Prefabs/" + name + ".prefab";
                var contents = PrefabUtility.LoadPrefabContents(path);
                try { add(contents); PrefabUtility.SaveAsPrefabAsset(contents, path); }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
            }
            AssetDatabase.SaveAssets();
            Debug.Log("SCALPAL_ENVIRONMENT_COLLISION_APPLIED");
        }

        static GameObject CreateModel(string name)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Models/" + name + ".fbx");
            if (!asset) throw new InvalidOperationException("Missing model: " + name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = renderer.sharedMaterials.Select(GetMaterial).ToArray();
            return instance;
        }

        static Material GetMaterial(Material source)
        {
            if (!source) throw new InvalidOperationException("Imported material missing.");
            string name = source.name;
            string path = Root + "Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("No compatible preview shader found.");
            if (!material) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader; material.color = source.color;
            float metallic = name == "steel" ? 0.85f : name == "glass" ? 0.4f : 0;
            float smoothness = name == "steel" ? 0.66f : name == "glass" ? 0.93f : 0.25f;
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Bounds GetBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("No model geometry.");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        static int Triangles(GameObject root) => root.GetComponentsInChildren<MeshFilter>(true)
            .Sum(filter => filter.sharedMesh ? filter.sharedMesh.triangles.Length / 3 : 0);

        public static void Validate()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                var room = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/OperatingTheatre.prefab");
                var patient = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/SupinePatient.prefab");
                if (!room || !patient) throw new InvalidOperationException("Preview prefabs missing.");
                int roomTriangles = Triangles(room), patientTriangles = Triangles(patient);
                int roomRenderers = room.GetComponentsInChildren<Renderer>(true).Length;
                if (roomTriangles != 12168 || roomRenderers != 8 || patientTriangles != 1578)
                    throw new InvalidOperationException("Prepared source geometry changed; inspect the imports.");
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                var body = roots.Single(o => o.name == "PatientRoot").GetComponentsInChildren<Renderer>(true).Single();
                if (Mathf.Abs(body.bounds.size.z - 1.75f) > 0.02f || body.bounds.size.y > 0.6f || Mathf.Abs(body.bounds.min.y - 0.98f) > 0.005f)
                    throw new InvalidOperationException("Patient must be metric, reclining and on the authored table height.");
                foreach (var root in roots)
                {
                    if (root.GetComponentsInChildren<Component>(true).Any(component => component == null))
                        throw new InvalidOperationException("Preview has a missing script.");
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        if (renderer.sharedMaterials.Any(material => !material || !material.shader))
                            throw new InvalidOperationException("Preview has a missing material or shader.");
                    foreach (var mesh in root.GetComponentsInChildren<MeshFilter>(true))
                        if (!mesh.sharedMesh) throw new InvalidOperationException("Preview has a missing mesh.");
                }
                Debug.Log($"SCALPAL_ENVIRONMENT_VALIDATION_OK: room {roomTriangles} triangles / {roomRenderers} renderers, patient {patientTriangles} triangles, 1.75m reclining fit. Editor only; no headset validation.");
            }
            finally { if (previous.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previous); }
        }
    }
}
