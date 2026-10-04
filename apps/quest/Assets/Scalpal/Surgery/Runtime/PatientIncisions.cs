using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Visible incisions where a triggered blade meets the VR patient's skin outside the surgical field (inside it
    // the open wall and the wound view own the incision). Bounded persistent stroke segments in registered torso
    // metres feeds Scalpal/PatientSkin, which draws the parted cut, its reddened margin and ooze running downhill.
    // Presentation only: segments come from tracked blade poses on the skin collider and wetness from the
    // interaction's region injury facts. Nothing here is scored or sent to the coach. AR has no virtual body.
    public sealed class PatientIncisions : MonoBehaviour
    {
        public const int Capacity = 64;
        // Authored look, not measured tissue behaviour. No wound/stain expiry within an attempt.
        public const float SpacingMeters = .004f, TrickleMetersPerSecond = .005f, MaxTrickleMeters = .05f;
        public struct Segment
        {
            public Vector3 start, end, normal; // torso metres; normal is the skin's outward normal
            public float depth, opening, trickle, age;
            public bool wet;
            public string region;
            public int stroke;
        }
        sealed class Stroke { public int id; public bool sampled; public Vector3 last; public float length, depth; }
        static readonly int CountId = Shader.PropertyToID("_ScalpalIncisionCount"), StartsId = Shader.PropertyToID("_ScalpalIncisionA"),
            EndsId = Shader.PropertyToID("_ScalpalIncisionB"), WorldToLocalId = Shader.PropertyToID("_ScalpalIncisionWorldToLocal"),
            GravityId = Shader.PropertyToID("_ScalpalIncisionGravity");
        readonly Segment[] ring = new Segment[Capacity];
        readonly Vector4[] starts = new Vector4[Capacity], ends = new Vector4[Capacity];
        readonly Dictionary<InstrumentBehaviour, Stroke> strokes = new Dictionary<InstrumentBehaviour, Stroke>();
        readonly List<InstrumentBehaviour> lifted = new List<InstrumentBehaviour>();
        OpenBodyInteraction input;
        Transform torso, wound;
        Collider skin;
        OpenWoundView fieldView;
        Func<bool> virtualBody;
        int head, count, strokeIds;
        BodyState attemptBody;
        bool initialized;
        // Registration/privacy may hide presentation without clearing accepted attempt history.
        public Func<bool> PresentationVisible;
        public int Count => count;
        // Ring slot order; the shader does not care which segment is oldest.
        public Segment this[int index] => ring[index];
        public bool Visible => torso && (virtualBody == null || virtualBody()) && (PresentationVisible == null || PresentationVisible());
        public static float ShaderCount => Shader.GetGlobalFloat(CountId);
        public static Vector4[] ShaderStarts => Shader.GetGlobalVectorArray(StartsId);
        public static Vector4[] ShaderEnds => Shader.GetGlobalVectorArray(EndsId);

        // skin: the patient's outward-wound collision copy of the rendered body; virtualBody: false in AR.
        public void Initialize(OpenBodyInteraction interaction, Transform torsoFrame, Transform woundFrame, Collider patientSkin, Func<bool> showsVirtualBody)
        {
            var binding = interaction ? interaction.GetComponentInParent<AnatomyExerciseBinding>() : null;
            var nextBody = binding ? binding.Body : null;
            bool fresh = !initialized || !ReferenceEquals(attemptBody, nextBody) || input != interaction || torso != torsoFrame;
            if (input) input.BladeOutsideField -= Blade;
            input = interaction; torso = torsoFrame; wound = woundFrame; skin = patientSkin; virtualBody = showsVirtualBody;
            attemptBody = nextBody; initialized = true;
            fieldView = wound ? wound.GetComponent<OpenWoundView>() : null;
            if (input) input.BladeOutsideField += Blade;
            if (fresh) Clear(); else Feed();
        }
        // A new attempt starts with unbroken skin.
        public void Clear()
        {
            head = count = 0; strokes.Clear();
            Feed();
        }
        // Most recent cut in a region, for placing that region's bleed on the skin.
        public bool TryRegionPoint(string region, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            for (int i = 1; i <= count; i++)
            {
                var segment = ring[(head - i + Capacity) % Capacity];
                if (segment.region != region) continue;
                point = (segment.start + segment.end) * .5f; normal = segment.normal; return true;
            }
            return false;
        }

        // The ray from the grip to the tip enters the skin where the blade does; how far the tip lies under the
        // surface is the depth. A blade that has not reached the skin, or a body that is not shown, leaves nothing.
        void Blade(InstrumentBehaviour tool)
        {
            if (!Visible || !skin || !skin.enabled || !tool || !tool.actionPoint || !tool.gripAnchor) return;
            Vector3 tip = tool.actionPoint.position, along = tip - tool.gripAnchor.position;
            float reach = along.magnitude;
            if (!OpenSurgeryStroke.Finite(tip) || reach < .01f || !skin.Raycast(new Ray(tool.gripAnchor.position, along / reach), out var hit, reach)) return;
            // Only the actual live incision aperture belongs to the wound presenter. The former
            // rectangle swallowed nearby punctures even though opaque patient skin was still visible.
            if (fieldView && fieldView.ClipsSkinAt(hit.point)) return;
            float depth = Mathf.Max(0, Vector3.Dot(hit.point - tip, hit.normal));
            Vector3 local = torso.InverseTransformPoint(hit.point), normal = torso.InverseTransformDirection(hit.normal).normalized;
            string region = OpenBodyInteraction.BodyRegion(torso.InverseTransformPoint(tip));
            if (strokes.TryGetValue(tool, out var stroke) && Vector3.Distance(stroke.last, local) > .05f) stroke = null; // a jump is not one cut
            if (stroke == null)
            {
                // First touch: a puncture, which the following motion extends into a cut.
                stroke = new Stroke { id = ++strokeIds, last = local, depth = depth }; strokes[tool] = stroke;
                Add(stroke, local, local, normal, region);
            }
            stroke.sampled = true; stroke.depth = Mathf.Max(stroke.depth, depth);
            float step = Vector3.Distance(stroke.last, local);
            if (step < SpacingMeters) return;
            stroke.length += step;
            Add(stroke, stroke.last, local, normal, region);
            stroke.last = local; stroke.depth = depth;
        }
        void Add(Stroke stroke, Vector3 start, Vector3 end, Vector3 normal, string region)
        {
            // The fixed shader budget must not look like spontaneous healing: retain existing wounds when
            // full. A subsequent decal/atlas backend can add detail beyond this explicit presentation cap.
            if (count == Capacity) return;
            ring[head] = new Segment { start = start, end = end, normal = normal, depth = stroke.depth, region = region, stroke = stroke.id, wet = true };
            head = (head + 1) % Capacity; count = Mathf.Min(count + 1, Capacity);
            // Deeper and longer strokes part wider; every segment of this stroke follows its length.
            for (int i = 0; i < count; i++)
                if (ring[i].stroke == stroke.id) ring[i].opening = Mathf.Clamp(.0003f + ring[i].depth * .08f + stroke.length * .006f, .0003f, .0022f);
        }

        // Once per frame after the interaction sampled the tools.
        public void Simulate(float seconds)
        {
            // A blade not sampled on the skin this frame has lifted: its stroke ends.
            lifted.Clear();
            foreach (var pair in strokes) if (!pair.Value.sampled) lifted.Add(pair.Key); else pair.Value.sampled = false;
            foreach (var tool in lifted) strokes.Remove(tool);
            if (Visible && input && input.Ready && float.IsFinite(seconds) && seconds > 0 && seconds <= .1f)
                for (int i = 0; i < count; i++)
                {
                    ref var segment = ref ring[i];
                    segment.age += seconds;
                    // Accepted control dries a regional cut; neither control nor time heals the incision or
                    // removes its stain. Unclassified cosmetic cuts have no accepted control fact or timeout.
                    bool bleeding = segment.region != "" && input && input.IsRegionInjured(segment.region);
                    if (segment.region != "") segment.wet = bleeding;
                    if (!segment.wet) continue;
                    float cap = MaxTrickleMeters * (.35f + .65f * Mathf.Clamp01(segment.depth / .006f));
                    segment.trickle = Mathf.Min(cap, segment.trickle + seconds * TrickleMetersPerSecond * (bleeding ? 2 : 1));
                }
            Feed();
        }
        void Feed()
        {
            for (int i = 0; i < count; i++)
            {
                var segment = ring[i];
                float trickle = segment.trickle + .0001f;
                starts[i] = new Vector4(segment.start.x, segment.start.y, segment.start.z, segment.opening);
                ends[i] = new Vector4(segment.end.x, segment.end.y, segment.end.z, segment.wet ? trickle : -trickle);
            }
            if (torso)
            {
                Shader.SetGlobalMatrix(WorldToLocalId, torso.worldToLocalMatrix);
                Shader.SetGlobalVector(GravityId, torso.InverseTransformDirection(Vector3.down).normalized);
            }
            Shader.SetGlobalVectorArray(StartsId, starts); Shader.SetGlobalVectorArray(EndsId, ends);
            Shader.SetGlobalFloat(CountId, Visible ? count : 0);
        }
        void OnEnable() { if (torso) Feed(); }
        void OnDisable() => Shader.SetGlobalFloat(CountId, 0);
        void OnDestroy() { if (input) input.BladeOutsideField -= Blade; }
    }
}
