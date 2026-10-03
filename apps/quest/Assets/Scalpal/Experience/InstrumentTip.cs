// Put on the trigger collider at an instrument's working end (the "Tip" child of Stephen's
// instrument prefabs). Two separate signals:
// - Focus: raw contact. Tells Jarvis what the tip is near, so "careful, that's the common bile duct"
//   lands before anything is scored. Never scores.
// - Touch: scored. Only while the instrument is held, tracked, and activated (trigger >= 0.7), once
//   per structure per activation cycle, matching InstrumentTipContact. Releasing the trigger
//   (<= 0.2) starts a new cycle, so multi-clip steps count each squeeze.
// Port sites place on entry: putting a trocar at the marked site is the action.
// Structures resolve by AnatomyPart.stableId (anatomy atlas), falling back to an "anat_<id>" name.
// Use this instead of subscribing to InstrumentTipContact.TouchApplied, never both, or touches double count.
using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Experience
{
    [DisallowMultipleComponent]
    public sealed class InstrumentTip : MonoBehaviour
    {
        const float ActivatedThreshold = 0.7f;
        const float ReleasedThreshold = 0.2f;

        [Tooltip("Catalog id. Blank reads it from the parent InstrumentBehaviour.")]
        public string instrumentId = "";
        [Tooltip("Blank uses the active CaseDirector in the scene.")]
        public CaseDirector director;
        [Tooltip("Desktop testing without a controller: treat the tool as held and activated.")]
        public bool forceActivated;

        InstrumentBehaviour instrument;
        readonly HashSet<string> scoredThisCycle = new HashSet<string>(StringComparer.Ordinal);

        void Awake()
        {
            instrument = GetComponentInParent<InstrumentBehaviour>();
            if (string.IsNullOrEmpty(instrumentId) && instrument != null) instrumentId = instrument.instrumentId ?? "";
        }

        void OnDisable() { scoredThisCycle.Clear(); }

        CaseDirector Director => director != null ? director : CaseDirector.Active;

        bool Activated => forceActivated || instrument != null && instrument.Held && instrument.TrackingValid && instrument.Activation >= ActivatedThreshold;

        bool Released => !forceActivated && (instrument == null || !instrument.Held || !instrument.TrackingValid || instrument.Activation <= ReleasedThreshold);

        void Update()
        {
            if (Released) scoredThisCycle.Clear();
        }

        void OnTriggerEnter(Collider other)
        {
            var d = Director;
            if (d == null || other == null) return;
            var port = other.GetComponent<PortTarget>();
            if (port != null)
            {
                d.PlacePort(port.portId);
                return;
            }
            var id = StructureId(other);
            if (id != null) d.Focus(id);
            TryScore(d, id);
        }

        void OnTriggerStay(Collider other)
        {
            var d = Director;
            if (d == null || other == null || other.GetComponent<PortTarget>() != null) return;
            TryScore(d, StructureId(other));
        }

        void OnTriggerExit(Collider other)
        {
            var d = Director;
            if (d != null && other != null && StructureId(other) != null) d.Focus("");
        }

        void TryScore(CaseDirector d, string structureId)
        {
            if (structureId == null || !Activated || !scoredThisCycle.Add(structureId)) return;
            d.Touch(structureId, instrumentId);
        }

        static string StructureId(Collider other)
        {
            var part = other.GetComponentInParent<AnatomyPart>();
            if (part != null && !string.IsNullOrEmpty(part.stableId)) return part.stableId;
            for (var t = other.transform; t != null; t = t.parent)
            {
                if (t.name.StartsWith("anat_", StringComparison.Ordinal)) return t.name.Substring(5);
            }
            return null;
        }
    }
}
