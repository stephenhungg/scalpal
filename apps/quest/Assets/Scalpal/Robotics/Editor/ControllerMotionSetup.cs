// One click: Scalpal > Robotics > Add Controller Motion Capture. Adds the capture to the open scene and points
// its pose frame at the scene's PatientRoot when there is one. Batch: AddToNativeSession does the same for the
// OR scene. The coach post uses UnityWebRequest, which already brings the Android internet permission.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Robotics.EditorTools
{
    public static class ControllerMotionSetup
    {
        public const string NativeSessionScene = "Assets/Scalpal/Quest/Scenes/NativeSession.unity";

        [MenuItem("Scalpal/Robotics/Add Controller Motion Capture")]
        static void Add()
        {
            var capture = Ensure();
            Selection.activeGameObject = capture.gameObject;
            Debug.Log("[Scalpal.Robotics] Controller capture ready. Mark-incision frames post to the coach when the step completes.");
        }

        public static ControllerMotionCapture Ensure()
        {
            var capture = Object.FindFirstObjectByType<ControllerMotionCapture>();
            if (capture == null)
            {
                var go = new GameObject("Controller Motion Capture");
                Undo.RegisterCreatedObjectUndo(go, "Controller Motion Capture");
                capture = Undo.AddComponent<ControllerMotionCapture>(go);
            }
            if (capture.PoseRoot == null)
            {
                var patient = GameObject.Find("PatientRoot");
                if (patient != null)
                {
                    Undo.RecordObject(capture, "Controller Motion Pose Root");
                    capture.PoseRoot = patient.transform;
                }
                else Debug.LogWarning("[Scalpal.Robotics] No PatientRoot in this scene; poses stay in world space. Assign Pose Root by hand.");
            }
            EditorSceneManager.MarkSceneDirty(capture.gameObject.scene);
            return capture;
        }

        // -executeMethod Scalpal.Robotics.EditorTools.ControllerMotionSetup.AddToNativeSession
        public static void AddToNativeSession()
        {
            var scene = EditorSceneManager.OpenScene(NativeSessionScene, OpenSceneMode.Single);
            var capture = Ensure();
            if (!EditorSceneManager.SaveScene(scene)) throw new System.InvalidOperationException("Could not save " + NativeSessionScene);
            Debug.Log("SCALPAL_ROBOT_CAPTURE_ADDED poseRoot=" + (capture.PoseRoot ? capture.PoseRoot.name : "none"));
        }
    }
}
