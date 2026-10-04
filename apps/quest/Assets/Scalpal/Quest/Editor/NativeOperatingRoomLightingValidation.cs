using System;
using System.Linq;
using Scalpal.Surgery;
using Scalpal.Surgery.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Quest.Editor
{
    public static class NativeOperatingRoomLightingValidation
    {
        [MenuItem("Scalpal/Quest/Validate Surgical Lighting")]
        public static void Run()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup(); NativeTissueSimulation tissue = null;
            NativeOperatingRoomLighting lighting = null; int checks = 0;
            void Require(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException("OR lighting: " + message); }
            try
            {
                var surgery = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                lighting = session.GetComponent<NativeOperatingRoomLighting>(); if (lighting) lighting.Dispose();
                var materials = session.gameObject.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Renderer>(true))
                    .SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToArray();
                var shaders = materials.Select(m => m.shader).ToArray();
                var originalColors = materials.Select(m => m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.HasProperty("_Color") ? m.color : Color.clear).ToArray();
                Require(materials.Length > 10 && materials.Any(m => m.shader.name == "Scalpal/PatientSkin"), "actual native materials and cut-aware patient shader are present");
                Require(materials.Any(m => m.HasProperty("_Glossiness") || m.HasProperty("_Smoothness")), "actual tissue materials expose PBR smoothness rather than an unlit substitute");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    foreach (var material in materials.Where(m => m.shader.name.StartsWith("Scalpal/") || m.shader.name == "Standard"))
                        Require(material.shader.isSupported, "actual source shader is supported by the active graphics device: " + material.shader.name);
                var roomKey = session.gameObject.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true)).Single(l => l.name == "WorkbenchLight");
                float roomIntensity = roomKey.intensity;
                int logCount = session.exercise.Body.Log.Count;
                float window = Shader.GetGlobalFloat("_ScalpalWoundWindow"), incisionCount = Shader.GetGlobalFloat("_ScalpalIncisionCount");
                Matrix4x4 woundMatrix = Shader.GetGlobalMatrix("_ScalpalWoundWorldToLocal");
                session.presentation.passthrough = false; session.presentation.Apply(); session.anatomy.SetRegistrationValid(true);
                if (!lighting) lighting = session.gameObject.AddComponent<NativeOperatingRoomLighting>();
                lighting.Initialize(session); lighting.Refresh();
                Require(lighting.Anchor == surgery.Wound.transform && lighting.Illuminating, "VR surgical illumination uses the real registered wound");
                var key = lighting.SurgicalKey; var fill = lighting.SoftFill;
                Require(key.type == LightType.Spot && key.renderMode == LightRenderMode.ForcePixel && fill.type == LightType.Point && fill.renderMode == LightRenderMode.ForceVertex,
                    "budget is one pixel spotlight plus one vertex fill");
                Require(key.shadows == LightShadows.None && fill.shadows == LightShadows.None && !key.cookie && !fill.cookie && key.bounceIntensity == 0 && fill.bounceIntensity == 0,
                    "rig adds no shadows, cookies or indirect bounce work");
                Require(key.range <= 1.4f && fill.range <= 1.1f && key.intensity <= .9f && fill.intensity <= .25f && key.spotAngle < 65,
                    "range/cone/intensity stay bounded around the field");
                Require(!key.transform.IsChildOf(session.presentation.virtualRoom.transform), "AR-hidden room does not own the light rig");
                Require(Mathf.Approximately(roomKey.intensity, roomIntensity * .55f), "broad room key is reduced to avoid stacking full room and surgical illumination");
                Vector3 expected = surgery.Wound.transform.position - surgery.Wound.transform.forward * .65f + surgery.Wound.transform.right * .12f + surgery.Wound.transform.up * .10f;
                Require(Vector3.Distance(expected, key.transform.position) < .00001f, "key is metric and wound-relative");
                Require(Vector3.Dot(key.transform.forward, (surgery.Wound.transform.position - surgery.Wound.transform.forward * .025f - key.transform.position).normalized) > .9999f,
                    "key aims at the actual surgical surface");
                Vector3 before = key.transform.position;var patient = session.patientFrame.root;
                patient.SetPositionAndRotation(patient.position + new Vector3(.3f, .12f, -.2f), Quaternion.Euler(12, 37, -8) * patient.rotation);
                lighting.Refresh(); expected = surgery.Wound.transform.position - surgery.Wound.transform.forward * .65f + surgery.Wound.transform.right * .12f + surgery.Wound.transform.up * .10f;
                Require(Vector3.Distance(before, key.transform.position) > .1f && Vector3.Distance(expected, key.transform.position) < .00001f, "actual patient movement and rotation move the surgical lighting consistently");
                session.presentation.passthrough = true; session.presentation.SyntheticCompositorReady = true; session.presentation.Apply(); lighting.Refresh();
                Require(!session.presentation.virtualRoom.activeSelf && lighting.Illuminating, "AR virtual anatomy remains lit with the entire virtual room hidden");
                session.anatomy.SetRegistrationValid(false); lighting.Refresh();
                Require(!lighting.Illuminating && !key.enabled && !fill.enabled && Mathf.Approximately(roomKey.intensity, roomIntensity), "lost registration disables both lights and restores room lighting");
                session.anatomy.SetRegistrationValid(true); lighting.Refresh(); Require(lighting.Illuminating, "restored registration reuses the same rig");
                session.presentation.SyntheticCompositorReady = false; lighting.Refresh();
                Require(!lighting.Illuminating, "AR compositor unavailable cannot enable virtual patient illumination");
                session.presentation.passthrough = false; session.presentation.Apply(); lighting.Refresh();
                lighting.enabled = false; lighting.Refresh(); Require(!lighting.Illuminating && Mathf.Approximately(roomKey.intensity, roomIntensity), "disabled component switches off lights and restores the source key");
                lighting.enabled = true; lighting.Refresh(); Require(lighting.Illuminating, "resumed component restores illumination without reallocation");
                Require(session.exercise.Body.Log.Count == logCount && Shader.GetGlobalFloat("_ScalpalWoundWindow") == window
                    && Shader.GetGlobalFloat("_ScalpalIncisionCount") == incisionCount && Shader.GetGlobalMatrix("_ScalpalWoundWorldToLocal") == woundMatrix,
                    "lighting does not emit progress or overwrite wound/cut shader globals");
                for (int i = 0; i < materials.Length; i++)
                    Require(materials[i].shader == shaders[i] && (materials[i].HasProperty("_BaseColor") ? materials[i].GetColor("_BaseColor") : materials[i].HasProperty("_Color") ? materials[i].color : Color.clear) == originalColors[i],
                        "source material/shader/color remains unchanged: " + materials[i].name);
                lighting.Dispose(); Require(!lighting.SurgicalKey && !lighting.SoftFill && !lighting.Anchor && Mathf.Approximately(roomKey.intensity, roomIntensity), "disposal removes transient lights and restores source lighting");
                lighting.Initialize(null); Require(!lighting.Illuminating, "missing session creates no unregistered light rig");
                Debug.Log("SCALPAL_NATIVE_OR_LIGHTING_OK: " + checks + " actual-scene transform/gate/material checks; synthetic AR compositor; shader capability "
                    + (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? "skipped (Null graphics device)" : "checked on active device") + "; no Quest GPU timing or clinical illumination claim");
            }
            finally { if (lighting) UnityEngine.Object.DestroyImmediate(lighting); if (tissue) tissue.Dispose(); OpenSurgeryBuild.RestoreScenes(previous); }
        }
    }
}
