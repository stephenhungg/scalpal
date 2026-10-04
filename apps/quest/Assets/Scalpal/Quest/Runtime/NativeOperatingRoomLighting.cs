using Scalpal.Surgery;
using UnityEngine;

namespace Scalpal.Quest
{
    // Virtual surgical illumination, not illumination of a real participant. The shared
    // registration/presentation gates own readiness. No material, shader global or body fact
    // changes; one bounded forward spotlight plus a cheap vertex fill, without shadow maps.
    [DisallowMultipleComponent, DefaultExecutionOrder(320)]
    public sealed class NativeOperatingRoomLighting : MonoBehaviour
    {
        NativeCaseSession session;
        OpenSurgerySession surgery;
        GameObject rig;
        Light key, fill, roomKey;
        float roomIntensity, appliedRoomIntensity;
        bool roomDimmed;
        public Light SurgicalKey => key;
        public Light SoftFill => fill;
        public Transform Anchor { get; private set; }
        public bool Illuminating => key && fill && key.enabled && fill.enabled;

        public void Initialize(NativeCaseSession owner)
        {
            Dispose(); session = owner;
            if (!owner) return;
            surgery = owner.GetComponent<OpenSurgerySession>();
            rig = new GameObject("RegisteredSurgicalLighting");
            rig.transform.SetParent(owner.transform, false);
            key = MakeLight("SurgicalKey", LightType.Spot, .85f, 1.35f, LightRenderMode.ForcePixel);
            key.color = new Color(1f, .975f, .94f); key.spotAngle = 58; key.innerSpotAngle = 34;
            fill = MakeLight("SurgicalSoftFill", LightType.Point, .22f, 1.05f, LightRenderMode.ForceVertex);
            fill.color = new Color(.90f, .95f, 1f);
            // The native scene's broad workbench key was authored for the empty room. Reduce
            // only that known light while the local rig is active, then restore its original
            // value. Other scene lights and ambient/reflection settings keep their owner.
            foreach (var root in owner.gameObject.scene.GetRootGameObjects())
                foreach (var light in root.GetComponentsInChildren<Light>(true))
                    if (light.name == "WorkbenchLight" && light.type == LightType.Directional)
                    { roomKey = light; roomIntensity = light.intensity; break; }
            Refresh();
        }

        Light MakeLight(string name, LightType type, float intensity, float range, LightRenderMode mode)
        {
            var node = new GameObject(name); node.transform.SetParent(rig.transform, false);
            var light = node.AddComponent<Light>(); light.type = type; light.intensity = intensity;
            light.range = range; light.renderMode = mode; light.shadows = LightShadows.None;
            light.bounceIntensity = 0; light.enabled = false;
            light.cullingMask = session.workbench && session.workbench.headCamera ? session.workbench.headCamera.cullingMask : ~0;
            return light;
        }

        public void Refresh()
        {
            if (!key || !fill || !session) return;
            if (!surgery) surgery = session.GetComponent<OpenSurgerySession>();
            Anchor = surgery && surgery.Wound ? surgery.Wound.transform : session.patientFrame;
            bool ready = isActiveAndEnabled && Anchor && Anchor.gameObject.activeInHierarchy
                && session.exercise && session.exercise.Body != null && session.anatomy && session.anatomy.RegistrationValid
                && (!session.presentation || session.presentation.Ready);
            if (Anchor)
            {
                // Wound +Z points inward. Torso +Y points outward in the fallback frame.
                Vector3 outward = surgery && surgery.Wound ? -Anchor.forward : Anchor.up;
                Vector3 tangent = Anchor.right, along = surgery && surgery.Wound ? Anchor.up : Anchor.forward;
                Vector3 target = Anchor.position + outward * .025f;
                key.transform.position = Anchor.position + outward * .65f + tangent * .12f + along * .10f;
                key.transform.rotation = Quaternion.LookRotation(target - key.transform.position, along);
                fill.transform.position = Anchor.position + outward * .45f - tangent * .25f - along * .06f;
            }
            key.enabled = fill.enabled = ready;
            if (ready && roomKey && !roomDimmed)
            {
                // Re-snapshot on each activation in case its owner changed the room in between.
                roomIntensity = roomKey.intensity; appliedRoomIntensity = roomIntensity * .55f;
                roomKey.intensity = appliedRoomIntensity; roomDimmed = true;
            }
            else if (!ready) RestoreRoomKey();
        }
        void RestoreRoomKey()
        {
            if (roomDimmed && roomKey && Mathf.Approximately(roomKey.intensity, appliedRoomIntensity)) roomKey.intensity = roomIntensity;
            roomDimmed = false;
        }
        void LateUpdate() => Refresh();
        void OnDisable() { if (key) key.enabled = false; if (fill) fill.enabled = false; RestoreRoomKey(); }
        void OnDestroy() => Dispose();
        public void Dispose()
        {
            RestoreRoomKey();
            if (rig) { rig.SetActive(false); if (Application.isPlaying) Destroy(rig); else DestroyImmediate(rig); }
            rig = null; key = fill = roomKey = null; session = null; surgery = null; Anchor = null;
        }
    }
}
