using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Retains a deliberately placed clamp/retractor relative to the registered body after release.
    // Picking it up again restores the normal tool owner; this does not award a body-state fact.
    [DisallowMultipleComponent]
    public sealed class SurgeryInstrumentLatch : MonoBehaviour
    {
        InstrumentBehaviour tool;
        Transform anchor;
        Rigidbody body;
        Vector3 localPosition;
        Quaternion localRotation;
        bool armed, released, changedBody, oldKinematic;
        public bool Attached => armed;
        public System.Func<bool> CanRetain;
        public void Attach(InstrumentBehaviour instrument, Transform tissueFrame)
        {
            Clear();
            if (!instrument || !tissueFrame) return;
            tool = instrument; anchor = tissueFrame; body = instrument.GetComponent<Rigidbody>();
            localPosition = anchor.InverseTransformPoint(tool.transform.position);
            localRotation = Quaternion.Inverse(anchor.rotation) * tool.transform.rotation;
            armed = true;
        }
        void LateUpdate()
        {
            if (!armed) return;
            if (!tool || !anchor) { Clear(); return; }
            if (tool.Held)
            {
                if (released) Clear();
                return;
            }
            released = true;
            if (body && !changedBody) { oldKinematic = body.isKinematic; body.isKinematic = true; changedBody = true; }
            if (CanRetain != null && !CanRetain()) return;
            tool.transform.SetPositionAndRotation(anchor.TransformPoint(localPosition), anchor.rotation * localRotation);
        }
        public void Clear()
        {
            // A newly held tool's rigidbody belongs to its grab controller again.
            if (body && changedBody && (!tool || !tool.Held)) body.isKinematic = oldKinematic;
            armed = released = changedBody = false; anchor = null;
        }
        void OnDisable() => Clear();
    }
}
