using System;
using System.IO;
using Scalpal.EncounterOffice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Handoff.Editor
{
    /// <summary>Uses the actual runtime card and brand assets; screenshots are mono editor evidence only.</summary>
    public static class HandoffCardPreview
    {
        const string Office = "Assets/Scalpal/EncounterOffice";
        const string ResourcesPath = "Assets/Scalpal/Handoff/Resources";
        public const string PrefabPath = ResourcesPath + "/HandoffCard.prefab";

        [MenuItem("Scalpal/Handoff/Prepare Card Resource")]
        public static void PrepareResources()
        {
            if (!AssetDatabase.IsValidFolder(ResourcesPath)) AssetDatabase.CreateFolder("Assets/Scalpal/Handoff", "Resources");
            var go = new GameObject("HandoffCard");
            try
            {
                var card = go.AddComponent<HandoffCard>(); Bind(card);
                PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // Fonts and materials come from the shared brand resource (Resources/ScalpalBrand); nothing to bind.
        public static void Bind(HandoffCard card) { if (!Scalpal.Brand.ScalpalBrand.Active) throw new InvalidOperationException("Brand resource missing."); }

        [MenuItem("Scalpal/Handoff/Capture Theatre and Time-Out")]
        public static void CapturePreviews()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var previousSetup = EditorSceneManager.GetSceneManagerSetup();
            string output = Environment.GetEnvironmentVariable("SCALPAL_HANDOFF_PREVIEW");
            if (string.IsNullOrWhiteSpace(output)) output = Path.GetFullPath("../../artifacts/handoff");
            if (!Path.IsPathRooted(output)) throw new InvalidOperationException("SCALPAL_HANDOFF_PREVIEW must be an absolute directory.");
            try
            {
                EditorSceneManager.OpenScene(Office + "/Scenes/DiagnosisOffice.unity", OpenSceneMode.Single);
                foreach (var existing in UnityEngine.Object.FindObjectsByType<EncounterOfficePanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    existing.gameObject.SetActive(false);
                var rig = UnityEngine.Object.FindFirstObjectByType<EncounterOfficeRig>();
                if (!rig || !rig.head) throw new InvalidOperationException("Office preview camera is not bound.");
                var camera = rig.head;
                camera.stereoTargetEye = StereoTargetEyeMask.None; camera.fieldOfView = 58;
                var card = new GameObject("PreviewHandoffCard").AddComponent<HandoffCard>(); Bind(card); card.viewer = camera;
                Directory.CreateDirectory(output);
                card.Show("To theatre", "Priya Ramaswamy, 40\nLaparoscopic appendectomy · Urgent\nVolunteer patient: virtual organs on a real person.\nVirtual OR: a virtual patient in the operating room.\nCamera/spatial permissions and tracking services are ready.",
                    new[] { "Volunteer patient (AR) · Recommended", "Virtual OR (VR)" }, null);
                Capture(camera, Path.Combine(output, "theatre.png"));
                card.Show("Time-Out", "Priya Ramaswamy, 40 · Urgent\nLaparoscopic appendectomy · Abdomen\nFOUND: Penicillin allergy\nMISSED: Anticoagulation history\nScalpal is your coach. Confirm patient and procedure.\nMistakes are expected; this is practice.",
                    new[] { "Confirm and begin practice", "Change theatre" }, null);
                Capture(camera, Path.Combine(output, "time-out.png"));
                card.Show("To theatre", "Priya Ramaswamy, 40\nLaparoscopic appendectomy · Urgent\nAR unavailable: body detection offline.\nContinue in the virtual operating room.",
                    new[] { "Volunteer patient (AR) · Unavailable", "Virtual OR (VR) · Recommended" }, null, new[] { false, true });
                Capture(camera, Path.Combine(output, "theatre-ar-unavailable.png"));
                Debug.Log("SCALPAL_HANDOFF_PREVIEW_OK monoEditorOnly=true output=" + output);
            }
            finally
            {
                if (Array.Exists(previousSetup, scene => scene.isLoaded && scene.isActive && !string.IsNullOrEmpty(scene.path)))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void Capture(Camera camera, string path)
        {
            var render = new RenderTexture(1920, 1440, 24);
            var previous = RenderTexture.active;
            var previousTarget = camera.targetTexture;
            var texture = new Texture2D(1920, 1440, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = render;
                camera.Render();
                RenderTexture.active = render;
                texture.ReadPixels(new Rect(0, 0, 1920, 1440), 0, 0); texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget; RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(render);
            }
        }
    }
}
