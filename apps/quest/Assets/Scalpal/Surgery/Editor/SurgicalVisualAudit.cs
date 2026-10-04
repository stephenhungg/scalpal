using UnityEditor;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Graphics-backed Editor audit of the actual scene, driven by synthetic tracked poses.
    // Never a physical playthrough, calibrated tissue measurement or clinical validation.
    public static class SurgicalVisualAudit
    {
        [MenuItem("Scalpal/Surgery/Render And Validate Surgical Visual Sequence")]
        public static void Run()
        {
            if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new System.InvalidOperationException("Visual audit requires a graphics device; omit -nographics.");
            OpenWoundAppearanceValidation.Run();
            SurgicalOrganAppearanceValidation.Run();
            SurgicalActionAppearanceValidation.Run();
            SurgicalClosureAppearanceValidation.Run();
            SurgicalWoundPersistenceValidation.Run();
            MarkingGuideValidation.Run();
            OpenStepVisualsValidation.Run();
            Debug.Log("SCALPAL_SURGICAL_VISUAL_AUDIT_OK: real-scene synthetic tool/body fixtures and graphics-backed stage renders; inspect images separately; no headset/clinical certification");
        }
    }
}
