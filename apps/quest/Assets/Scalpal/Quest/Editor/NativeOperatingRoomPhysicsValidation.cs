using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Quest.Editor
{
    // Committed native scene in the Editor, not a headset test. The VR patient must read as a real body:
    // solid skin rather than a see-through ghost. AR keeps hiding the mannequin entirely.
    public static class NativeOperatingRoomPhysicsValidation
    {
        static int checks;
        static void Assert(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Operating room physics validation: " + message);
        }

        [MenuItem("Scalpal/Quest/Validate Operating Room Patient and Physics")]
        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                var scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Single);
                NativeCaseSession session = null;
                foreach (var root in scene.GetRootGameObjects()) { var found = root.GetComponentInChildren<NativeCaseSession>(true); if (found) session = found; }
                Assert(session && session.presentation && session.presentation.virtualMannequin, "scene binds the VR mannequin");
                PatientIsSolid(session.presentation);
                Debug.Log("SCALPAL_OR_PHYSICS_VALIDATION_OK checks=" + checks + " editorOnly=true headsetValidated=false");
            }
            finally
            {
                if (previous.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // A transparent, depth-less body lets the table, organs and floor show through the patient in VR.
        static void PatientIsSolid(NativePresentation view)
        {
            var materials = view.virtualMannequin.sharedMaterials;
            Assert(materials.Length > 0, "VR mannequin has a material");
            foreach (var material in materials)
            {
                Assert(material && material.shader, "VR mannequin material and shader exist");
                Assert(material.renderQueue < (int)RenderQueue.AlphaTest && material.GetTag("RenderType", false) == "Opaque",
                    "VR patient renders in the opaque queue: " + material.name + " queue=" + material.renderQueue);
                Assert(!material.HasProperty("_Color") || Mathf.Approximately(material.color.a, 1f), "VR patient skin has full alpha: " + material.name);
                Assert(!material.HasProperty("_ZWrite") || material.GetFloat("_ZWrite") >= 1f, "VR patient writes depth so it occludes what is behind it");
                Assert(!material.IsKeywordEnabled("_ALPHABLEND_ON") && !material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), "VR patient has no alpha blending");
                Assert(material.GetShaderPassEnabled("ShadowCaster"), "VR patient casts shadows like a solid body");
            }
        }
    }
}
