// One click: Scalpal > Robotics > Add Controller Motion Capture. Adds the capture to the open scene and
// enables the Android internet permission its UDP stream needs.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Robotics.EditorTools
{
    static class ControllerMotionSetup
    {
        [MenuItem("Scalpal/Robotics/Add Controller Motion Capture")]
        static void Add()
        {
            var capture = Object.FindFirstObjectByType<ControllerMotionCapture>();
            if (capture == null)
            {
                var go = new GameObject("Controller Motion Capture");
                Undo.RegisterCreatedObjectUndo(go, "Controller Motion Capture");
                capture = Undo.AddComponent<ControllerMotionCapture>(go);
            }
            PlayerSettings.Android.forceInternetPermission = true;
            EditorSceneManager.MarkSceneDirty(capture.gameObject.scene);
            Selection.activeGameObject = capture.gameObject;
            Debug.Log("[Scalpal.Robotics] Controller capture ready. Set Host to the Mac's LAN IP, then on the Mac run: cd services/motion && uv run scalpal-motion teleop");
        }
    }
}
