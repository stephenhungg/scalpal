using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Tracked metric measurements -> the existing scored path. BodyState determines effects;
    // neither expected order nor current case step is consulted here.
    [DisallowMultipleComponent]
    public sealed class OpenBodyInteraction : MonoBehaviour
    {
        sealed class ToolState
        {
            public InstrumentBehaviour tool;
            public Transform bladeStart, bladeEnd;
            public string instance, tissueId = "", clampedTissueId = "", touched = "";
            public SurgeryTissueTarget target;
            public SurgeryInstrumentLatch latch;
            public OrganMobilization mobile;
            public readonly OpenSurgeryStroke stroke = new OpenSurgeryStroke();
            public readonly List<Vector3> marker = new List<Vector3>();
            public Vector3 previous, contact, rawSurface, handleOffset, speedOrigin;
            public bool active, previousValid, committed, grasping, ownsHandle;
            public float strokeSent, dwell, sinceSent, speed, speedClock;
            public BodyAction lastTentMeasurement;
            // Physical wall material grip (0 = none). A latched retractor keeps holding its tissue
            // after the hand lets go, as retainedToken, until it is picked up or unlatched.
            public int layerToken, retainedToken, splitTriedWith;
            public string retainedTissueId = "";
            public Vector3 layerOffset;
            // Wall fracture counts when this blade engaged the wall: a stroke scores only the layer it opened.
            public bool wallEngaged;
            public readonly long[] wallCutBaseline = new long[OpenWallLayers.Count];
            // Tissue a held tool is resting on without the trigger (no effect), for the first-use affordance.
            public string idleTouched = "";
        }
        // Authored teaching thresholds for coupling paired retraction to the volume, not measured mechanics.
        const float SplitOnsetMm = .5f, SplitHalfLengthMeters = .03f, SplitDepthMarginMeters = .0005f, BladeContactMeters = .003f;
        readonly List<ToolState> states = new List<ToolState>();
        readonly List<SurgeryTissueTarget> targets = new List<SurgeryTissueTarget>();
        readonly Dictionary<string, BodyAction> pendingTentReleases = new Dictionary<string, BodyAction>();
        readonly List<OrganMobilization> mobility = new List<OrganMobilization>();
        readonly long[] cutFractures = new long[OpenWallLayers.Count];
        // Regions outside the surgical field reported as injured and not yet controlled.
        readonly HashSet<string> injuredRegions = new HashSet<string>();
        NativeVolumeSimulation wall;
        // Exposure facts change only when the body appends a record; per-frame reads use this copy.
        BodyState cachedBody;
        int cachedLogCount = -1;
        bool[] tissueOpened = Array.Empty<bool>();
        double activeBleeds;
        AnatomyExerciseBinding exercise;
        Transform torso, wound;
        Func<bool> gate;
        BodyState attemptBody;
        string attemptPrefix;
        int sequence;
        double activeSeconds;
        float tickClock;
        bool wasReady, hasMarkedLine, frameKnown;
        Matrix4x4 lastTorsoFrame, lastWoundFrame;
        Vector3 referenceStart = new Vector3(-.03f, 0, 0), referenceEnd = new Vector3(.03f, 0, 0), markedStart, markedEnd;
        public event Action<BodyRecord, InstrumentBehaviour> Submitted;
        public event Action<IReadOnlyList<Vector3>> MarkerChanged;
        // State tracker (never scored): a tool tip first touching a tissue, and region injury/control outside the field.
        public event Action<InstrumentBehaviour, string> Contacted;
        public event Action<string, InstrumentBehaviour, bool> RegionInjured;
        // A held, tracked tool touching tissue with the trigger released: nothing is applied, so tell the learner
        // which input acts (tool, verb, tissue). Edge-triggered per touch; never scored or sent to the coach.
        public event Action<InstrumentBehaviour, string, string> TouchedWithoutTrigger;
        public string LastRejection { get; private set; } = "";
        public double ActiveSeconds => activeSeconds;
        public double TimeMs => activeSeconds * 1000;
        public bool Ready => isActiveAndEnabled && exercise && exercise.CanScore && exercise.Body != null && torso && wound && gate != null && gate();
        public IReadOnlyList<SurgeryTissueTarget> Targets => targets;
        public IReadOnlyList<OrganMobilization> Mobility => mobility;

        // The open-wall volume supplies layer contact, grips, lift/split measurements and cut evidence.
        // Without one, wall layers keep authored contact planes but can never be tented or split.
        public void BindWall(NativeVolumeSimulation volume)
        {
            if (wall == volume) return;
            ClearTransient();
            if (wall) wall.LayerFractured -= Fractured;
            wall = volume;
            if (wall) wall.LayerFractured += Fractured;
        }
        void Fractured(NativeVolumeSimulation.LayerFracture fracture)
        {
            if (fracture.verb == "cut" && fracture.newlyBrokenFaces > 0 && OpenWallLayers.TryGet(fracture.layerId, out var layer))
                cutFractures[layer.index] += fracture.newlyBrokenFaces;
        }

        public void Initialize(AnatomyExerciseBinding binding, InstrumentBehaviour[] tools, Transform torsoFrame, Transform woundFrame, Func<bool> canInteract)
        {
            ClearTransient(); foreach (var state in states) { if (state.latch) state.latch.Clear(); ReleaseRetained(state); }
            foreach (var group in mobility) group.RestoreRest();
            states.Clear(); targets.Clear(); mobility.Clear();
            exercise = binding; torso = torsoFrame; wound = woundFrame; gate = canInteract;
            foreach (var tool in tools ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!tool || !BodyState.ToolVerbs.ContainsKey(tool.instrumentId)) continue;
                var state = new ToolState { tool = tool, instance = "tool-" + tool.GetInstanceID().ToString().Replace("-", "n") };
                int starts = 0, ends = 0;
                foreach (var anchor in tool.GetComponentsInChildren<Transform>(true))
                {
                    if (anchor.GetComponentInParent<InstrumentBehaviour>() != tool) continue;
                    if (anchor.name == "CutStart") { state.bladeStart = anchor; starts++; }
                    if (anchor.name == "CutEnd") { state.bladeEnd = anchor; ends++; }
                }
                if (starts != 1 || ends != 1) state.bladeStart = state.bladeEnd = null;
                states.Add(state);
            }
            if (binding && binding.anatomy)
            {
                foreach (var target in binding.anatomy.GetComponentsInChildren<SurgeryTissueTarget>(true)) AddTarget(target);
                // Existing owned atlas targets need only an identity adapter. Longitudinal base/end
                // references are deliberately not guessed from mesh bounds.
                foreach (var definition in binding.Body?.Tissues ?? Array.Empty<TissueDefinition>())
                {
                    if (definition.order >= 0) continue;
                    var structureIds = definition.structureIds != null && definition.structureIds.Length > 0
                        ? definition.structureIds : new[] { definition.id };
                    foreach (var structureId in structureIds)
                    {
                        if (!binding.anatomy.TryGetPart(structureId, out var part)) continue;
                        var target = part.GetComponent<SurgeryTissueTarget>() ?? part.gameObject.AddComponent<SurgeryTissueTarget>();
                        target.tissueId = definition.id; AddTarget(target);
                    }
                }
            }
            ResetInteractions();
        }
        void AddTarget(SurgeryTissueTarget target)
        {
            if (!target || targets.Contains(target)) return;
            target.Refresh(); targets.Add(target);
        }
        // Groups come from scene/case data. Each attempt starts with every group at its authored rest pose.
        public bool ConfigureMobility(IEnumerable<MobileOrganGroup> groups, out string status)
        {
            ClearTransient(); foreach (var group in mobility) group.RestoreRest();
            mobility.Clear(); status = "";
            bool all = true;
            foreach (var definition in groups ?? Array.Empty<MobileOrganGroup>())
            {
                var group = OrganMobilization.Create(exercise ? exercise.anatomy : null, definition, out var reason);
                if (group == null) { all = false; status += (status.Length > 0 ? "; " : "") + reason; continue; }
                if (mobility.Exists(other => { foreach (var part in group.Parts) if (other.Contains(part)) return true; return false; }))
                { all = false; status += (status.Length > 0 ? "; " : "") + "mobile groups overlap"; continue; }
                mobility.Add(group);
            }
            return all;
        }
        public void SetTargets(SurgeryTissueTarget[] supplied)
        {
            ClearTransient(); targets.Clear();
            foreach (var target in supplied ?? Array.Empty<SurgeryTissueTarget>()) AddTarget(target);
        }
        public void ResetInteractions()
        {
            ClearTransient(); foreach (var state in states) { if (state.latch) state.latch.Clear(); ReleaseRetained(state); state.clampedTissueId = ""; }
            foreach (var group in mobility) group.RestoreRest(); // A retry starts from the authored anatomy.
            pendingTentReleases.Clear(); // A previous attempt's measurements cannot enter its replacement.
            injuredRegions.Clear();
            attemptBody = exercise ? exercise.Body : null;
            attemptPrefix = Guid.NewGuid().ToString("N"); sequence = 0; activeSeconds = 0; tickClock = 0;
            LastRejection = ""; wasReady = false; hasMarkedLine = false; frameKnown = false;
        }
        // Equipment reset discards placement/pose history, while preserving the scored attempt.
        public void ClearPlacements()
        {
            ClearTransient();
            foreach (var state in states) { if (state.latch) state.latch.Clear(); ReleaseRetained(state); }
        }
        // The owner calls this once per update, after tracked tool poses are refreshed.
        public void Simulate(float seconds)
        {
            if (exercise && attemptBody != exercise.Body) ResetInteractions();
            if (!Ready || !float.IsFinite(seconds) || seconds <= 0 || seconds > .1f)
            {
                if (wasReady) ClearTransient();
                wasReady = false; frameKnown = false; return;
            }
            Matrix4x4 torsoFrame = torso.localToWorldMatrix, woundFrame = wound.localToWorldMatrix;
            bool frameChanged = frameKnown && (torsoFrame != lastTorsoFrame || woundFrame != lastWoundFrame);
            lastTorsoFrame = torsoFrame; lastWoundFrame = woundFrame; frameKnown = true;
            // A stroke measured before a fit correction is still one real stroke; commit it, then restart.
            if (frameChanged) { foreach (var state in states) FlushStroke(state); ClearTransient(); }
            wasReady = true; activeSeconds += seconds; tickClock += seconds;
            // Release invalid grasps before processing any blade, regardless of tool array order.
            foreach (var state in states)
                if (state.grasping && (!ValidTool(state) || !OpenSurgeryStroke.Finite(state.tool.actionPoint.position)
                    || (state.previousValid && Vector3.Distance(state.tool.actionPoint.position, state.previous) > .05f))) Release(state);
            DrainTentReleases();
            // A placed clamp that was picked up again or put away no longer occludes anything.
            foreach (var state in states)
                if (state.clampedTissueId != "" && (!state.latch || !state.latch.Attached)) ReleaseClamp(state);
            // A latched retractor that is picked up again or unlatched stops holding its tissue.
            foreach (var state in states)
                if (state.retainedToken != 0 && (!state.latch || !state.latch.Attached || !state.tool || state.tool.Held)) ReleaseRetained(state);
            // A fit correction is not tool travel. The next stable sample starts new anchors and
            // stroke history with zero speed; the registered active-time clock still advances.
            if (!frameChanged)
            {
                foreach (var state in states) SampleTool(state, seconds);
                // Released groups ease back unless delivered; held groups moved with their tool above.
                // A fit correction is not organ motion, so a correction frame never settles a group.
                foreach (var group in mobility) group.Settle(seconds, wound);
            }
            // Blood loss accrues during stalls through a 1 Hz tick, only while a bleed is active.
            RefreshBodyFacts();
            if (activeBleeds <= 0) tickClock = 0;
            else if (tickClock >= 1)
            {
                tickClock %= 1;
                var tissue = FirstTissue();
                if (tissue != null) SubmitMeasured(Action("assistant", "clock", "tick", tissue, wound.position), null);
            }
        }
        void RefreshBodyFacts()
        {
            var body = exercise.Body;
            if (body == cachedBody && body.Log.Count == cachedLogCount) return;
            cachedBody = body; cachedLogCount = body.Log.Count;
            if (tissueOpened.Length != body.Tissues.Length) tissueOpened = new bool[body.Tissues.Length];
            for (int i = 0; i < tissueOpened.Length; i++) tissueOpened[i] = body.Get(body.Tissues[i].id, "opened") > 0;
            activeBleeds = body.Get("", "activeBleeds");
        }
        bool Opened(TissueDefinition tissue)
        {
            RefreshBodyFacts();
            for (int i = 0; i < tissueOpened.Length; i++) if (cachedBody.Tissues[i] == tissue) return tissueOpened[i];
            return false;
        }
        TissueDefinition FirstTissue() => exercise?.Body?.Tissues.Length > 0 ? exercise.Body.Tissues[0] : null;
        TissueDefinition Definition(string id)
        {
            // Per-frame lookup without a capturing lambda (no Quest GC allocation).
            foreach (var tissue in exercise.Body.Tissues) if (tissue.id == id) return tissue;
            return null;
        }
        bool ValidTool(ToolState state) => state.tool && state.tool.isActiveAndEnabled && state.tool.Held && state.tool.TrackingValid && state.tool.Activation >= .7f && state.tool.actionPoint;

        void SampleTool(ToolState state, float seconds)
        {
            if (!ValidTool(state))
            {
                if (state.active && state.tool && state.tool.TrackingValid) FlushStroke(state);
                Release(state); IdleContact(state); return;
            }
            state.idleTouched = "";
            Vector3 point = state.tool.actionPoint.position;
            if (!OpenSurgeryStroke.Finite(point)) { Release(state); return; }
            state.speed = state.previousValid ? Vector3.Distance(point, state.previous) / seconds : 0;
            if (state.previousValid && Vector3.Distance(point, state.previous) > .05f) { FlushStroke(state); Release(state); return; }
            state.previous = point; state.previousValid = true; state.active = true;
            string verb = BodyState.ToolVerbs[state.tool.instrumentId][0];
            if (!state.grasping)
            {
                if (!FindContact(state, verb, point, out var definition, out var target, out var contact))
                { FlushStroke(state); ClearContact(state); state.wallEngaged = false; state.touched = ""; OutsideField(state, verb, point); return; }
                // Edge-triggered: one contact per touch of a tissue, not one per frame.
                if (state.touched != definition.id) { state.touched = definition.id; Contacted?.Invoke(state.tool, definition.id); }
                // Fracture counts before this blade's first wall contact; LateUpdate cuts follow this Update.
                if (verb == "cut" && wall && !target && OpenWallLayers.TryGet(definition.id, out _) && !state.wallEngaged)
                { Array.Copy(cutFractures, state.wallCutBaseline, cutFractures.Length); state.wallEngaged = true; }
                if (state.tissueId != definition.id)
                {
                    // Passing through an already opened layer to reach a deeper one is not another cut of it.
                    bool passThrough = verb == "cut" && wall && !state.target && !target && OpenWallLayers.TryGet(state.tissueId, out var from)
                        && OpenWallLayers.TryGet(definition.id, out var to) && to.index > from.index && Opened(Definition(state.tissueId));
                    if (!passThrough) FlushStroke(state);
                    ClearContact(state);
                    state.tissueId = definition.id; state.target = target; state.contact = contact;
                    Vector3 local = wound.InverseTransformPoint(contact);
                    Vector3 axis = target ? wound.InverseTransformDirection(target.LongitudinalWorld).normalized : Vector3.right;
                    state.stroke.ConfigureLine(local - axis * .03f, local + axis * .03f, Vector3.forward);
                    // Wall incision error is measured against the authored registered line, not the first touch.
                    if (!target) state.stroke.ConfigureLine(verb == "cut" && hasMarkedLine ? markedStart : referenceStart,
                        verb == "cut" && hasMarkedLine ? markedEnd : referenceEnd, Vector3.forward);
                }
            }
            var tissue = Definition(state.tissueId); if (tissue == null) return;
            state.sinceSent += seconds;
            if (verb == "cut" || verb == "mark")
            {
                Vector3 local = wound.InverseTransformPoint(point);
                bool wallLayer = OpenWallLayers.TryGet(tissue.id, out var layer);
                if (state.stroke.Sample(local, seconds, wallLayer ? layer.startDepthMeters : local.z))
                {
                    // One committed action per stroke (per layer): FlushStroke runs when contact ends,
                    // so guardrails see the finished stroke once instead of every millimetre.
                    if (verb == "mark" && (state.marker.Count == 0 || Vector3.Distance(state.marker[state.marker.Count - 1], local) > .001f)) state.marker.Add(local);
                }
                return;
            }
            if (verb == "grasp" || verb == "retract") { SampleGrasp(state, tissue, point, seconds); return; }
            if (verb == "suction")
            {
                state.dwell += seconds;
                if (state.sinceSent < .2f) return;
                var suction = Action(state.tool.instrumentId, state.instance, "suction", tissue, point);
                suction.durationMs = state.sinceSent * 1000; suction.speedMps = state.speed;
                SubmitMeasured(suction, state.tool);
                var inspection = Action(state.tool.instrumentId, state.instance, "inspect", tissue, point);
                inspection.durationMs = state.dwell * 1000;
                SubmitMeasured(inspection, state.tool); state.sinceSent = 0; return;
            }
            if (state.committed) return;
            var action = Action(state.tool.instrumentId, state.instance, verb, tissue, point);
            // Hemostasis is positional, so a seal's place along the structure is measured like a clamp's.
            if ((verb == "clamp" || verb == "tie" || verb == "seal") && state.target)
            {
                bool measured = state.target.DistanceFromBase(point, out float along); action.distanceMm = along;
                if (!measured) action.choice = "longitudinal_unmeasured";
            }
            if (SubmitMeasured(action, state.tool))
            {
                state.committed = true;
                if (verb == "clamp" && EffectApplied(action.actionId))
                {
                    // One physical clamp: applying it to another tissue first takes it off the previous one.
                    if (state.clampedTissueId != "" && state.clampedTissueId != state.tissueId) ReleaseClamp(state);
                    Latch(state); state.clampedTissueId = state.tissueId;
                }
            }
        }

        // Same geometric contact as an activated tool, but only reported: tool effects stay trigger-driven.
        void IdleContact(ToolState state)
        {
            var tool = state.tool;
            string touched = "";
            if (tool && tool.isActiveAndEnabled && tool.Held && tool.TrackingValid && tool.actionPoint && OpenSurgeryStroke.Finite(tool.actionPoint.position))
            {
                string verb = BodyState.ToolVerbs[tool.instrumentId][0];
                if (FindContact(state, verb, tool.actionPoint.position, out var definition, out _, out _)) touched = definition.id;
                if (touched != "" && touched != state.idleTouched) TouchedWithoutTrigger?.Invoke(tool, verb, touched);
            }
            state.idleTouched = touched;
        }

        void FlushStroke(ToolState state)
        {
            if (!Ready || !state.tool || string.IsNullOrEmpty(state.tissueId) || state.stroke.PathLengthMm < 1 || (state.target ? state.stroke.PathLengthMm : state.stroke.LengthMm) <= state.strokeSent) return;
            var tissue = Definition(state.tissueId); if (tissue == null) return;
            string verb = BodyState.ToolVerbs[state.tool.instrumentId][0];
            if (verb != "cut" && verb != "mark") return;
            OpenWallLayers.Layer layer = default;
            bool wallLayer = !state.target && OpenWallLayers.TryGet(tissue.id, out layer);
            // With a wall volume, a blade stroke counts only if it actually opened that material.
            if (verb == "cut" && wallLayer && wall && (!state.wallEngaged || cutFractures[layer.index] <= state.wallCutBaseline[layer.index]))
            { LastRejection = "Blade stroke did not open the " + tissue.id + " tissue."; return; }
            Vector3 point = wound.TransformPoint(state.stroke.End);
            var action = Action(state.tool.instrumentId, state.instance, verb, tissue, point);
            action.lengthMm = state.target ? state.stroke.PathLengthMm : state.stroke.LengthMm; action.distanceMm = state.stroke.ErrorMm;
            action.angleDegrees = state.stroke.AngleDegrees; action.depthMm = state.stroke.DepthMm;
            // Fibered layers report the stroke against their authored fiber axis, not the skin line.
            if (verb == "cut" && wallLayer && layer.hasFibers && OpenWallLayers.TryFiberAngle(tissue.id, state.stroke.End - state.stroke.Start, out float fiberAngle))
                action.angleDegrees = fiberAngle;
            action.durationMs = state.stroke.DurationMs; action.speedMps = state.speed;
            if (state.target) { bool measured = state.target.DistanceFromBase(point, out float along); action.distanceMm = along; if (!measured) action.choice = "longitudinal_unmeasured"; }
            if (verb == "mark")
            {
                Vector3 midpoint = (state.stroke.Start + state.stroke.End) * .5f;
                action.distanceMm = Vector3.ProjectOnPlane(midpoint - (referenceStart + referenceEnd) * .5f, Vector3.forward).magnitude * 1000;
            }
            if (SubmitMeasured(action, state.tool))
            {
                if (verb == "cut" && wallLayer && wall) state.wallCutBaseline[layer.index] = cutFractures[layer.index];
                state.strokeSent = state.target ? state.stroke.PathLengthMm : state.stroke.LengthMm;
                if (verb == "mark")
                {
                    markedStart = state.stroke.Start; markedEnd = state.stroke.End;
                    hasMarkedLine = Vector3.Distance(markedStart, markedEnd) >= .001f;
                    MarkerChanged?.Invoke(state.marker);
                }
            }
        }

        void SampleGrasp(ToolState state, TissueDefinition tissue, Vector3 point, float seconds)
        {
            // A hidden layer may generate an attempted event, but cannot acquire a physical handle.
            foreach (var barrier in exercise.Body.Tissues)
            {
                if (barrier.order < 0 || (tissue.order >= 0 && barrier.order >= tissue.order) || Opened(barrier)) continue;
                if (!state.committed)
                {
                    SubmitMeasured(Action(state.tool.instrumentId, state.instance, BodyState.ToolVerbs[state.tool.instrumentId][0], tissue, point), state.tool);
                    state.committed = true;
                }
                return;
            }
            if (!state.grasping)
            {
                state.grasping = true; state.speedOrigin = point; state.speedClock = 0;
                if (state.target)
                {
                    var group = mobility.Find(candidate => !candidate.Held && candidate.Contains(state.target.transform));
                    if (group != null && group.BeginHold(state.contact)) state.mobile = group;
                }
                if (state.target) state.rawSurface = state.target.transform.InverseTransformPoint(state.contact);
                // A wall layer is held by an actual material patch at its current exposed surface.
                else if (wall && wall.TryBeginLayerHandle(tissue.id, point, Mathf.Min(state.tool.contactRadius, .006f), out int token))
                {
                    state.layerToken = token; state.splitTriedWith = 0;
                    state.layerOffset = wall.TryMeasureLayerHandle(token, out var grip) ? grip.worldPosition - point : Vector3.zero;
                }
                var deformable = state.target ? state.target.Deformable : null;
                if (deformable && deformable.Cage != null)
                {
                    state.rawSurface = deformable.transform.InverseTransformPoint(state.contact);
                    if (state.target.driveGrasp && deformable.Cage.Handle < 0 && deformable.Cage.BeginHandle(deformable.ToMeters(state.rawSurface)))
                    {
                        state.ownsHandle = true;
                        state.handleOffset = deformable.Cage.HandlePosition - deformable.ToMeters(deformable.transform.InverseTransformPoint(point));
                    }
                }
            }
            Vector3 measuredPoint = point;
            // The mobilized group follows the tool first; the cage then deforms about its new pose.
            if (state.mobile != null) state.mobile.Follow(point, seconds);
            if (state.target)
            {
                var deformable = state.target.Deformable;
                // Delivery is a tissue measurement. A moving tool with an unmoving organ earns none.
                if (deformable && deformable.Cage != null)
                {
                    if (state.ownsHandle)
                    {
                        deformable.Step(seconds, deformable.ToMeters(deformable.transform.InverseTransformPoint(point)) + state.handleOffset);
                        deformable.CommitSurface();
                    }
                    measuredPoint = deformable.transform.TransformPoint(deformable.DeformSurfacePoint(state.rawSurface));
                }
                else measuredPoint = state.target.transform.TransformPoint(state.rawSurface);
            }
            else
            {
                // Request the hand's pose; measure only geometry the solver accepted (read after its LateUpdate step).
                measuredPoint = state.contact;
                if (state.layerToken != 0 && (!wall || !wall.TrySetLayerHandleTarget(state.layerToken, point + state.layerOffset)))
                    state.layerToken = 0; // Gate, frame or topology loss released the grip; it is not resurrected.
                if (state.layerToken != 0 && wall.TryMeasureLayerHandle(state.layerToken, out var grip)) measuredPoint = grip.worldPosition;
                // Checked every frame: the split must be requested as soon as the pair actually parts.
                if (tissue.splittable && state.layerToken != 0) RequestSplit(state, tissue.id);
            }
            state.speedClock += seconds;
            if (state.sinceSent < .1f) return;
            var action = Action(state.tool.instrumentId, state.instance, BodyState.ToolVerbs[state.tool.instrumentId][0], tissue, measuredPoint);
            // Net hand travel over at least 100 ms, so per-frame tracking jitter cancels instead of reading as speed.
            action.speedMps = Vector3.Distance(point, state.speedOrigin) / Mathf.Max(state.speedClock, .1f);
            state.speedOrigin = point; state.speedClock = 0;
            // Tenting is the gripped membrane's accepted outward lift, never the controller's travel.
            action.depthMm = tissue.tentable ? CurrentTentLift(tissue.id) : Mathf.Max(0, -wound.InverseTransformPoint(measuredPoint).z) * 1000;
            if (tissue.splittable && state.layerToken != 0) MeasureSplit(state, tissue.id, action);
            if (SubmitMeasured(action, state.tool))
            {
                if (tissue.tentable && EffectApplied(action.actionId)) state.lastTentMeasurement = action.Copy();
                if (tissue.splittable && action.separationMm >= 15 && EffectApplied(action.actionId)) Latch(state);
            }
            state.sinceSent = 0;
        }
        void ReleaseClamp(ToolState state)
        {
            var tissue = Definition(state.clampedTissueId); state.clampedTissueId = "";
            if (tissue == null || !state.tool) return;
            Vector3 point = state.tool.actionPoint && OpenSurgeryStroke.Finite(state.tool.actionPoint.position) ? state.tool.actionPoint.position : wound.position;
            SubmitMeasured(Action(state.tool.instrumentId, state.instance, "release", tissue, point), state.tool);
        }
        bool EffectApplied(string actionId)
        {
            // A subscriber may synchronously append a fluid record; inspect this action's outcome.
            for (int i = exercise.Body.Log.Count - 1; i >= 0; i--)
            {
                var record = exercise.Body.Log[i];
                if (record.action.actionId != actionId) continue;
                return Array.IndexOf(record.outcomes, "not_exposed") < 0 && Array.IndexOf(record.outcomes, "missing_instance") < 0;
            }
            return false;
        }
        void Latch(ToolState state)
        {
            if (!state.latch) state.latch = state.tool.GetComponent<SurgeryInstrumentLatch>() ?? state.tool.gameObject.AddComponent<SurgeryInstrumentLatch>();
            state.latch.CanRetain = () => Ready;
            state.latch.Attach(state.tool, state.target ? state.target.transform : wound);
        }

        bool FindContact(ToolState state, string verb, Vector3 point, out TissueDefinition tissue, out SurgeryTissueTarget target, out Vector3 contact)
        {
            tissue = null; target = null; contact = point;
            float nearest = float.PositiveInfinity;
            foreach (var candidate in targets)
            {
                if (!candidate || Definition(candidate.tissueId) == null) continue;
                Vector3 hit = default;
                bool touched = verb == "cut" && state.bladeStart && state.bladeEnd && candidate.BladeContact(state.bladeStart.position, state.bladeEnd.position, out hit);
                if (!touched && !candidate.TryContact(point, Mathf.Min(state.tool.contactRadius, .006f), out hit)) continue;
                float distance = (point - hit).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance; target = candidate; contact = hit; tissue = Definition(candidate.tissueId);
            }
            // The teaching wall is finite. Selecting a layer depends only on geometry and exposure.
            Vector3 local = wound.InverseTransformPoint(point);
            if (wall) return WallContact(state, verb, point, local, ref nearest, ref tissue, ref target, ref contact) || tissue != null;
            // No volume: authored OpenWallLayers contact planes (no tenting or splitting is measurable).
            if (Mathf.Abs(local.x) > .065f || Mathf.Abs(local.y) > .04f) return tissue != null;
            for (int i = 0; i < OpenWallLayers.Count; i++)
            {
                var layer = OpenWallLayers.Get(i);
                var definition = Definition(layer.id); if (definition == null) continue;
                float depth = layer.startDepthMeters, distance = Mathf.Abs(local.z - depth);
                bool segment = false;
                if (verb == "cut" && state.bladeStart && state.bladeEnd)
                {
                    Vector3 a = wound.InverseTransformPoint(state.bladeStart.position), b = wound.InverseTransformPoint(state.bladeEnd.position);
                    float dz = b.z - a.z;
                    if (Mathf.Abs(dz) > .000001f)
                    {
                        float t = (depth - a.z) / dz;
                        Vector3 hit = Vector3.LerpUnclamped(a, b, t);
                        segment = t >= 0 && t <= 1 && Mathf.Abs(hit.x) <= .065f && Mathf.Abs(hit.y) <= .04f;
                        if (segment) distance = 0;
                    }
                }
                if (distance > .004f && !segment) continue;
                bool opened = Opened(definition);
                // Once opened, the center gap exposes the deeper layer; its lips remain interactable.
                if (opened && Mathf.Abs(local.y) < .007f && verb != "mark" && state.tissueId != definition.id) continue;
                float squared = distance * distance;
                if (squared >= nearest) continue;
                tissue = definition; target = null; contact = wound.TransformPoint(new Vector3(local.x, local.y, depth)); nearest = squared;
            }
            return tissue != null;
        }
        // Volume contact: each layer's current exposed boundary/cut faces near the tool (blade edge or tip).
        // The shallowest touched layer the body has not opened is engaged. Once every touched layer is open,
        // a stroke in progress keeps its layer (a blade outruns the faces it has just cut); otherwise the
        // deepest one is engaged (extending or re-entering a wound). A marker takes the nearest surface.
        bool WallContact(ToolState state, string verb, Vector3 point, Vector3 local, ref float nearest, ref TissueDefinition tissue, ref SurgeryTissueTarget target, ref Vector3 contact)
        {
            bool blade = verb == "cut" && state.bladeStart && state.bladeEnd;
            Vector3 a = local, b = local;
            if (blade) { a = wound.InverseTransformPoint(state.bladeStart.position); b = wound.InverseTransformPoint(state.bladeEnd.position); }
            // Broad phase against the 160 x 100 x 28 mm wall plus its bounded material excursion.
            if (!NearWall(a) && !NearWall(b)) return false;
            float radius = blade ? BladeContactMeters : Mathf.Min(state.tool.contactRadius, .006f);
            int samples = blade ? Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / .004f) + 1, 2, 6) : 1;
            int chosen = -1, deepest = -1; float chosenSquared = 0, deepestSquared = 0; Vector3 chosenHit = default, deepestHit = default;
            for (int i = 0; i < OpenWallLayers.Count; i++)
            {
                var definition = Definition(OpenWallLayers.Get(i).id); if (definition == null) continue;
                float best = float.PositiveInfinity; Vector3 bestHit = default;
                for (int sample = 0; sample < samples; sample++)
                {
                    Vector3 world = samples == 1 ? point : wound.TransformPoint(Vector3.Lerp(a, b, sample / (float)(samples - 1)));
                    if (!wall.TryContactLayer(definition.id, world, radius, out Vector3 hit)) continue;
                    float squared = (world - hit).sqrMagnitude;
                    if (squared < best) { best = squared; bestHit = hit; }
                }
                if (float.IsPositiveInfinity(best)) continue;
                if (verb == "mark") { if (chosen < 0 || best < chosenSquared) { chosen = i; chosenSquared = best; chosenHit = bestHit; } continue; }
                deepest = i; deepestSquared = best; deepestHit = bestHit;
                if (chosen < 0 && !Opened(definition)) { chosen = i; chosenSquared = best; chosenHit = bestHit; }
            }
            if (chosen < 0 && deepest >= 0 && verb != "mark" && !state.target && OpenWallLayers.TryGet(state.tissueId, out var current))
            { chosen = current.index; chosenHit = state.contact; chosenSquared = (point - state.contact).sqrMagnitude; }
            if (chosen < 0) { chosen = deepest; chosenSquared = deepestSquared; chosenHit = deepestHit; }
            if (chosen < 0 || chosenSquared >= nearest) return false;
            tissue = Definition(OpenWallLayers.Get(chosen).id); target = null; contact = chosenHit; nearest = chosenSquared;
            return true;
        }
        static bool NearWall(Vector3 local) => Mathf.Abs(local.x) <= .09f && Mathf.Abs(local.y) <= .06f && local.z >= -.03f && local.z <= .06f;
        // Registration owner supplies the landmark provenance; this never derives depth from images.
        public bool SetLandmarks(Vector3 worldRightAsis, Vector3 worldUmbilicus, Vector3 worldLineDirection, float lineLengthMeters = .06f)
        {
            if (!wound || !OpenSurgeryStroke.Finite(worldRightAsis) || !OpenSurgeryStroke.Finite(worldUmbilicus)
                || !OpenSurgeryStroke.Finite(worldLineDirection) || lineLengthMeters < .05f || lineLengthMeters > .08f) return false;
            Vector3 center = wound.InverseTransformPoint(OpenSurgeryStroke.McBurney(worldRightAsis, worldUmbilicus));
            Vector3 axis = Vector3.ProjectOnPlane(wound.InverseTransformDirection(worldLineDirection), Vector3.forward);
            if (axis.sqrMagnitude < .000001f) return false;
            referenceStart = center - axis.normalized * lineLengthMeters * .5f;
            referenceEnd = center + axis.normalized * lineLengthMeters * .5f;
            return true;
        }
        // Use only after an explicit assisted premark event has been accepted by the shared engine.
        public bool SetMarkedLine(Vector3 worldStart, Vector3 worldEnd)
        {
            if (!Ready || !OpenSurgeryStroke.Finite(worldStart) || !OpenSurgeryStroke.Finite(worldEnd) || Vector3.Distance(worldStart, worldEnd) < .001f) return false;
            markedStart = wound.InverseTransformPoint(worldStart); markedEnd = wound.InverseTransformPoint(worldEnd); hasMarkedLine = true;
            MarkerChanged?.Invoke(new[] { markedStart, markedEnd }); return true;
        }

        // External physical adapters use the same active attempt clock and unique ID allocator.
        public BodyAction CreateMeasurement(string instrument, string verb, string tissueId, Vector3 worldPoint, string instance = "system")
        {
            if (!Ready) return null;
            var tissue = Definition(tissueId);
            return tissue == null ? null : Action(instrument, instance, verb, tissue, worldPoint);
        }
        BodyAction Action(string instrument, string instance, string verb, TissueDefinition tissue, Vector3 world)
        {
            Vector3 local = torso.InverseTransformPoint(world);
            return new BodyAction { actionId = "body-" + attemptPrefix + "-" + (++sequence), instrumentId = instrument,
                instrumentInstanceId = instance, verb = verb, tissueId = tissue.id, layer = tissue.layer,
                coordinateFrame = "registered_torso_m", position = new Vec3 { x = local.x, y = local.y, z = local.z },
                registered = true, timeMs = activeSeconds * 1000 };
        }
        public bool SubmitMeasured(BodyAction action) => SubmitMeasured(action, null);
        bool SubmitMeasured(BodyAction action, InstrumentBehaviour tool)
        {
            if (!Ready || !BodyState.ValidBodyAction(action) || !action.registered || action.timeMs > activeSeconds * 1000 + .01)
            { LastRejection = "Practice unavailable or invalid measured body action."; return false; }
            action.Quantize(); // Event boundary: the body applies exactly the rounded values the coach receives.
            if (action.verb == "cut") DrainTentReleases();
            int count = exercise.Body.Log.Count;
            if (!exercise.Submit(CaseEvent.Surgery(action), out _, out var reason)) { LastRejection = reason; return false; }
            if (exercise.Body.Log.Count <= count) { LastRejection = "Body rejected duplicate, stale, or unknown action."; return false; }
            LastRejection = ""; Submitted?.Invoke(exercise.Body.Log[exercise.Body.Log.Count - 1], tool); return true;
        }
        public bool Choose(string tissueId, string choice)
        {
            if (!Ready || string.IsNullOrEmpty(choice)) return false;
            bool authored = false;
            foreach (var decision in exercise.SelectedCase.procedure.openBody.decisions ?? Array.Empty<BodyDecision>())
                if (Array.IndexOf(decision.choices ?? Array.Empty<string>(), choice) >= 0) authored = true;
            var tissue = Definition(tissueId);
            if (!authored || tissue == null) return false;
            var action = Action("decision", "learner", "decide", tissue, wound.position); action.choice = choice;
            return SubmitMeasured(action);
        }
        void ClearContact(ToolState state)
        {
            if (state.lastTentMeasurement != null)
                pendingTentReleases[state.lastTentMeasurement.tissueId] = state.lastTentMeasurement.Copy();
            state.lastTentMeasurement = null;
            if (state.ownsHandle && state.target && state.target.Deformable?.Cage != null) state.target.Deformable.Cage.ReleaseHandle();
            if (state.mobile != null) { state.mobile.EndHold(); state.mobile = null; }
            ReleaseGrip(state);
            state.tissueId = ""; state.target = null; state.grasping = state.ownsHandle = state.committed = false;
            state.stroke.Reset(); state.marker.Clear(); state.strokeSent = state.dwell = state.sinceSent = 0;
        }
        void DrainTentReleases()
        {
            if (!Ready || pendingTentReleases.Count == 0) return;
            foreach (var id in new List<string>(pendingTentReleases.Keys))
            {
                var release = pendingTentReleases[id].Copy();
                release.actionId = "body-" + attemptPrefix + "-" + (++sequence);
                release.timeMs = TimeMs; release.depthMm = 0; release.speedMps = 0;
                release.durationMs = 0; release.choice = "release";
                // The position is the last registered sample, not a guessed pose during tracking loss.
                // Another live forceps can still hold this same membrane up; measure that hold now.
                release.depthMm = CurrentTentLift(id);
                if (SubmitMeasured(release, null)) pendingTentReleases.Remove(id);
            }
        }
        // Accepted outward lift of any live grip on this membrane, including a latched one.
        float CurrentTentLift(string tissueId)
        {
            float maximum = 0;
            if (!wall) return maximum;
            foreach (var state in states)
            {
                int token = GripToken(state, tissueId);
                if (token == 0 || !wall.TryMeasureLayerHandle(token, out var grip) || !grip.hasAcceptedStep) continue;
                if (float.IsFinite(grip.outwardLiftMillimeters)) maximum = Mathf.Max(maximum, grip.outwardLiftMillimeters);
            }
            return maximum;
        }
        int GripToken(ToolState state, string tissueId) => state.layerToken != 0 && state.tissueId == tissueId ? state.layerToken
            : state.retainedToken != 0 && state.retainedTissueId == tissueId ? state.retainedToken : 0;
        // Accepted material positions of two grips in wall-local metres and the line their opening runs along.
        bool PairGeometry(int first, int second, out Vector3 a, out Vector3 b, out Vector3 along)
        {
            a = b = along = Vector3.zero;
            if (!wall.TryMeasureLayerHandle(first, out var one) || !wall.TryMeasureLayerHandle(second, out var two)) return false;
            Transform frame = wall.Wall.transform;
            a = frame.InverseTransformPoint(one.worldPosition); b = frame.InverseTransformPoint(two.worldPosition);
            Vector3 across = new Vector3(b.x - a.x, b.y - a.y, 0);
            if (across.sqrMagnitude < 1e-10f) return false;
            along = Vector3.Cross(Vector3.forward, across).normalized; return true;
        }
        // Two distinct material grips that actually pull apart request one finite split surface through
        // their midpoint. The volume refuses one that runs across the fibers, so only a pull across the
        // fibers (an opening along them) can split the muscle; a single grip never requests one.
        void RequestSplit(ToolState state, string tissueId)
        {
            foreach (var other in states)
            {
                int token = other == state ? 0 : GripToken(other, tissueId);
                if (token == 0) continue;
                if (state.splitTriedWith == token || other.splitTriedWith == state.layerToken) return;
                if (!wall.TryMeasureMuscleSplit(state.layerToken, token, out float increase) || increase < SplitOnsetMm
                    || !PairGeometry(state.layerToken, token, out var a, out var b, out var along) || !OpenWallLayers.TryGet(tissueId, out var layer)) continue;
                // One request per grip pair; the length is an authored gridiron-opening extent.
                state.splitTriedWith = token; other.splitTriedWith = state.layerToken;
                Transform frame = wall.Wall.transform;
                Vector3 middle = (a + b) * .5f, reach = along * SplitHalfLengthMeters;
                Vector3 top = new Vector3(middle.x, middle.y, layer.startDepthMeters - SplitDepthMarginMeters);
                Vector3 bottom = new Vector3(middle.x, middle.y, layer.endDepthMeters + SplitDepthMarginMeters);
                wall.TrySplitMuscle(frame.TransformPoint(top - reach), frame.TransformPoint(top + reach), frame.TransformPoint(bottom + reach), out _);
                wall.TrySplitMuscle(frame.TransformPoint(top - reach), frame.TransformPoint(bottom + reach), frame.TransformPoint(bottom - reach), out _);
                return;
            }
        }
        // The volume's measured separation increase across the fibers, and the opening line's fiber angle.
        void MeasureSplit(ToolState state, string tissueId, BodyAction action)
        {
            foreach (var other in states)
            {
                int token = other == state ? 0 : GripToken(other, tissueId);
                if (token == 0 || !wall.TryMeasureMuscleSplit(state.layerToken, token, out float increase)
                    || !PairGeometry(state.layerToken, token, out _, out _, out var along)) continue;
                action.separationMm = Mathf.Max(0, increase);
                action.angleDegrees = OpenWallLayers.TryFiberAngle(tissueId, along, out float angle) ? angle : 90;
                action.secondaryInstanceId = other.instance; return;
            }
        }
        void ReleaseGrip(ToolState state)
        {
            if (state.layerToken == 0) return;
            // A latched retractor let go by the hand keeps its tissue held open.
            if (wall && state.latch && state.latch.Attached && state.tool && !state.tool.Held && state.tissueId != "")
            { ReleaseRetained(state); state.retainedToken = state.layerToken; state.retainedTissueId = state.tissueId; }
            else if (wall) wall.ReleaseLayerHandle(state.layerToken);
            state.layerToken = 0;
        }
        void ReleaseRetained(ToolState state)
        {
            if (state.retainedToken != 0 && wall) wall.ReleaseLayerHandle(state.retainedToken);
            state.retainedToken = 0; state.retainedTissueId = "";
        }
        void Release(ToolState state) { ClearContact(state); state.active = state.previousValid = state.wallEngaged = false; state.touched = ""; }
        // Outside the surgical field the body is coarse regions (docs/operation-flow.md). A blade inside one reports it
        // once; a clamp, tie or seal inside an injured region reports it controlled. The coach owns the consequences.
        void OutsideField(ToolState state, string verb, Vector3 point)
        {
            string region = BodyRegion(torso.InverseTransformPoint(point));
            if (region == "") return;
            if (verb == "cut") { if (injuredRegions.Add(region)) RegionInjured?.Invoke(region, state.tool, false); }
            else if ((verb == "clamp" || verb == "tie" || verb == "seal") && injuredRegions.Remove(region)) RegionInjured?.Invoke(region, state.tool, true);
        }
        // Registered torso frame: umbilicus origin, +X patient left, +Y anterior, +Z cranial, source metres. Bounds come
        // from the imported surface atlas (lying supine, arms at the sides). The abdomen and pelvis between chest and
        // legs hold the surgical field and are not a region; points above the skin or below the back are not on the body.
        public static string BodyRegion(Vector3 p)
        {
            if (!OpenSurgeryStroke.Finite(p) || p.y < -.26f || p.z > .73f || p.z < -1.02f) return "";
            float x = Mathf.Abs(p.x); string side = p.x >= 0 ? "left_" : "right_";
            string region; float skin;
            if (p.z >= .512f) { if (x > .12f) return ""; region = "head"; skin = 0; }
            else if (p.z < .45f && p.z >= -.33f && x <= .35f && (x >= .183f || (x >= .158f && p.z >= .082f))) { region = side + "arm"; skin = p.z < -.17f ? -.01f : -.07f; }
            else if (p.z >= .405f && x < .158f) { region = "neck"; skin = -.03f; }
            else if (p.z >= .162f && x < .158f) { region = "chest"; skin = .02f; }
            else if (p.z < -.198f && x <= .2f) { region = side + "leg"; skin = p.z < -.9f ? .035f : -.03f; }
            else return "";
            return p.y <= skin ? region : "";
        }
        void ClearTransient() { foreach (var state in states) Release(state); }
        void OnDisable() { ClearPlacements(); wasReady = false; frameKnown = false; }
    }
}
