using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // One scoring adapter for the native scene. Do not also install Matthew's InstrumentTip.
    [DefaultExecutionOrder(100)]
    public sealed class NativeProcedureInput : MonoBehaviour
    {
        public NativePortMarker[] portMarkers = Array.Empty<NativePortMarker>();
        public float gazeDistance = 2.5f;
        public string FocusedPartId { get; private set; } = "";
        public string FocusedPortId { get; private set; } = "";
        AnatomyExerciseBinding exercise;
        NativeWorkbench workbench;
        Func<bool> practicing;
        readonly List<ContactBinding> contacts = new List<ContactBinding>();
        readonly Dictionary<InstrumentBehaviour, HashSet<string>> reported = new Dictionary<InstrumentBehaviour, HashSet<string>>();
        readonly RaycastHit[] gazeHits = new RaycastHit[48];
        readonly Collider[] overlaps = new Collider[48];

        sealed class ContactBinding
        {
            public InstrumentTipContact contact;
            public InstrumentBehaviour tool;
            public Action<string, string> handler;
            public Func<bool> gate;
        }

        public void Initialize(AnatomyExerciseBinding binding, NativeWorkbench nativeWorkbench, Func<bool> isPracticing)
        {
            Unbind();
            exercise = binding; workbench = nativeWorkbench; practicing = isPracticing;
            if (!workbench) return;
            foreach (var tool in workbench.tools ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!tool) continue;
                foreach (var contact in tool.GetComponentsInChildren<InstrumentTipContact>(true))
                {
                    if (contact.GetComponentInParent<InstrumentBehaviour>() != tool) continue;
                    var item = new ContactBinding { contact = contact, tool = tool };
                    item.gate = CanScore;
                    item.handler = (partId, instrumentId) => HandleTouch(item, partId, instrumentId);
                    contact.RegistrationIsValid = item.gate;
                    contact.TouchApplied += item.handler;
                    contacts.Add(item);
                }
            }
            if (portMarkers == null || portMarkers.Length == 0) portMarkers = GetComponentsInChildren<NativePortMarker>(true);
            foreach (var marker in portMarkers) if (marker) marker.input = this;
        }

        bool CanScore() => isActiveAndEnabled && exercise && workbench && workbench.IsReady &&
            practicing != null && practicing() && exercise.CanScore && !exercise.Completed;

        void Update()
        {
            foreach (var tool in workbench ? workbench.tools ?? Array.Empty<InstrumentBehaviour>() : Array.Empty<InstrumentBehaviour>())
                if (tool && (!tool.Held || !tool.TrackingValid || tool.Activation <= 0.2f)) reported.Remove(tool);
            RefreshFocus();
        }

        void HandleTouch(ContactBinding source, string partId, string instrumentId)
        {
            if (!CanScore() || exercise.Current?.check == null || exercise.Current.check.type == "place_ports" ||
                !ValidTool(source.tool, true) || instrumentId != source.tool.instrumentId || !source.contact || !source.contact.isActiveAndEnabled ||
                source.contact.GetComponentInParent<InstrumentBehaviour>() != source.tool) return;
            var owned = FindTouchCollider(source.tool, partId, source.contact);
            if (!owned || !Remember(source.tool, "part:" + partId)) return;
            if (!exercise.SelectInstrument(source.tool.instrumentId)) return;
            exercise.TouchCollider(owned, out _, out _);
        }

        Collider FindTouchCollider(InstrumentBehaviour tool, string partId, InstrumentTipContact sourceContact)
        {
            if (!exercise || !exercise.anatomy || !tool.actionPoint ||
                !exercise.anatomy.TryGetPart(partId, out var part) || !part.IsVisible || !part.HasVisibleGeometry) return null;
            foreach (var collider in part.GetComponentsInChildren<Collider>(true))
                if (OwnedPart(collider) == part && TipOverlaps(tool, collider, sourceContact)) return collider;
            return null;
        }

        AnatomyPart OwnedPart(Collider collider)
        {
            if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy || !exercise || !exercise.anatomy) return null;
            var part = collider.GetComponentInParent<AnatomyPart>(true);
            return part && part.IsVisible && part.HasVisibleGeometry && exercise.anatomy.TryGetPart(part.stableId, out var owned) && owned == part ? part : null;
        }

        bool ValidTool(InstrumentBehaviour tool, bool activated)
        {
            if (!tool || !tool.isActiveAndEnabled || !tool.Held || !tool.TrackingValid || !tool.actionPoint ||
                (activated && tool.Activation < 0.7f) || !workbench) return false;
            return Array.IndexOf(workbench.tools ?? Array.Empty<InstrumentBehaviour>(), tool) >= 0;
        }

        // Verify collision against a real trigger belonging to this tool's actual tip.
        static bool TipOverlaps(InstrumentBehaviour tool, Collider target, InstrumentTipContact sourceContact = null)
        {
            if (!target || !target.enabled || !target.gameObject.activeInHierarchy || !tool || !tool.actionPoint) return false;
            foreach (var contact in tool.GetComponentsInChildren<InstrumentTipContact>(true))
            {
                if (!contact.isActiveAndEnabled || contact.GetComponentInParent<InstrumentBehaviour>() != tool) continue;
                if (sourceContact && contact != sourceContact) continue;
                var tip = contact.GetComponent<Collider>();
                if (!tip || !tip.enabled || !tip.isTrigger || !tip.gameObject.activeInHierarchy ||
                    Vector3.Distance(contact.transform.position, tool.actionPoint.position) > tool.contactRadius) continue;
                if (Physics.ComputePenetration(tip, tip.transform.position, tip.transform.rotation,
                    target, target.transform.position, target.transform.rotation, out _, out _)) return true;
            }
            return false;
        }

        bool Remember(InstrumentBehaviour tool, string target)
        {
            if (!reported.TryGetValue(tool, out var cycle)) reported.Add(tool, cycle = new HashSet<string>(StringComparer.Ordinal));
            return cycle.Add(target);
        }

        NativePortMarker FindMarker(string id)
        {
            NativePortMarker match = null;
            foreach (var marker in portMarkers ?? Array.Empty<NativePortMarker>())
            {
                if (!marker || marker.input != this || marker.portId != id || !marker.isActiveAndEnabled) continue;
                if (match) return null; // Ambiguous scene bindings fail closed.
                match = marker;
            }
            return match;
        }

        public bool TryPlacePort(string portId, InstrumentBehaviour tool, out string reason)
        {
            if (!CanScore()) return Reject("Practice is not ready", out reason);
            if (!ValidTool(tool, true)) return Reject("Hold a tracked tool and activate its trigger", out reason);
            var step = exercise.Current;
            if (step?.check?.type != "place_ports") return Reject("This step does not place ports", out reason);
            if (tool.instrumentId != step.instrumentId) return Reject("Use this step's authored instrument", out reason);
            if (Array.IndexOf(step.check.targets ?? Array.Empty<string>(), portId) < 0) return Reject("Port is not a target of this step", out reason);
            Port authored = null;
            foreach (var port in exercise.SelectedCase?.procedure?.ports ?? Array.Empty<Port>())
            {
                if (port == null || port.id != portId) continue;
                if (authored != null) return Reject("Ambiguous authored port", out reason);
                authored = port;
            }
            if (authored == null || Array.IndexOf(authored.instrumentIds ?? Array.Empty<string>(), tool.instrumentId) < 0)
                return Reject("Instrument is not allowed at this port", out reason);
            var marker = FindMarker(portId);
            bool overlap = false;
            if (marker) foreach (var collider in marker.GetComponentsInChildren<Collider>(true))
                overlap |= marker.Owns(collider) && TipOverlaps(tool, collider);
            if (!overlap) return Reject("The actual tool tip is not touching this port marker", out reason);
            if (!Remember(tool, "port:" + portId)) return Reject("Port already handled in this trigger cycle", out reason);
            return exercise.Submit(CaseEvent.PlacePort(portId), out _, out reason);
        }

        public void RefreshFocus()
        {
            FocusedPartId = ""; FocusedPortId = "";
            if (!CanScore()) return;
            float closest = float.PositiveInfinity;
            foreach (var tool in workbench.tools ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!ValidTool(tool, false)) continue;
                int count = Physics.OverlapSphereNonAlloc(tool.actionPoint.position, tool.contactRadius, overlaps, ~0, QueryTriggerInteraction.Collide);
                for (int i = 0; i < count; i++)
                {
                    if (!TipOverlaps(tool, overlaps[i])) continue;
                    float distance = (overlaps[i].bounds.center - tool.actionPoint.position).sqrMagnitude;
                    if (distance < closest && SetFocus(overlaps[i])) closest = distance;
                }
            }
            if (!string.IsNullOrEmpty(FocusedPartId) || !string.IsNullOrEmpty(FocusedPortId) || !workbench.headCamera) return;
            var camera = workbench.headCamera.transform;
            int hits = Physics.RaycastNonAlloc(camera.position, camera.forward, gazeHits, Mathf.Max(0, gazeDistance), ~0, QueryTriggerInteraction.Collide);
            closest = float.PositiveInfinity;
            for (int i = 0; i < hits; i++)
                if (gazeHits[i].distance < closest && SetFocus(gazeHits[i].collider)) closest = gazeHits[i].distance;
        }

        bool SetFocus(Collider collider)
        {
            var part = OwnedPart(collider);
            if (part) { FocusedPartId = part.stableId; FocusedPortId = ""; return true; }
            var port = collider ? collider.GetComponentInParent<NativePortMarker>(true) : null;
            if (!port || FindMarker(port.portId) != port || !port.Owns(collider)) return false;
            FocusedPartId = ""; FocusedPortId = port.portId; return true;
        }

        public bool IdentifyFocused(out string reason)
        {
            RefreshFocus();
            if (!CanScore()) return Reject("Practice is not ready", out reason);
            if (exercise.Current?.check?.type != "identify_targets") return Reject("This step does not ask for identification", out reason);
            if (string.IsNullOrEmpty(FocusedPartId)) return Reject("Look at or touch a visible anatomy part", out reason);
            return exercise.Submit(CaseEvent.Identify(FocusedPartId), out _, out reason);
        }

        public bool PlaceFocusedPort(out string reason)
        {
            RefreshFocus();
            if (!CanScore() || string.IsNullOrEmpty(FocusedPortId)) return Reject("Touch an authored port with the active tool tip", out reason);
            foreach (var tool in workbench.tools ?? Array.Empty<InstrumentBehaviour>())
                if (TryPlacePort(FocusedPortId, tool, out reason)) return true;
            return Reject("No valid active tool tip overlaps the focused port", out reason);
        }

        public bool Confirm(out string reason)
        {
            if (!CanScore()) return Reject("Practice is not ready", out reason);
            if (exercise.Current?.check?.type != "confirm") return Reject("This step requires its authored action", out reason);
            return exercise.Submit(CaseEvent.Confirm(), out _, out reason);
        }

        void OnDisable() { Unbind(); FocusedPartId = ""; FocusedPortId = ""; }
        void OnEnable() { if (exercise && workbench) Initialize(exercise, workbench, practicing); }
        void OnDestroy() => Unbind();
        void Unbind()
        {
            foreach (var item in contacts)
            {
                if (!item.contact) continue;
                item.contact.TouchApplied -= item.handler;
                if (item.contact.RegistrationIsValid == item.gate) item.contact.RegistrationIsValid = null;
            }
            contacts.Clear(); reported.Clear();
        }
        static bool Reject(string value, out string reason) { reason = value; return false; }
    }
}
