using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Brand;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using TMPro;
using UnityEngine;

namespace Scalpal.Surgery
{
    // The open case's marking step, made visible. While the skin is intact the closed teaching wall stays under
    // the patient's skin (no skin window, coupon or wall slab), the landmarks are dotted and labelled, a dashed
    // guide shows the registered reference line the mark is measured against, and the marker's ink is drawn on
    // the visible skin while it is drawn. Accepted marking history fades the guide; a mark the milestone does not
    // accept gets one short reason on the hint line. Presentation only: it never submits, scores or moves anything.
    [DefaultExecutionOrder(140)] // LateUpdate after NativeVolumeSimulation (115) has shown its wall for the frame.
    [DisallowMultipleComponent]
    public sealed class MarkingGuide : MonoBehaviour
    {
        public const string Instruction = "Pick up the skin marker (grip).\nHold the trigger and draw along the dotted line.";
        public const string HipLabel = "Hip bone (ASIS)", NavelLabel = "Belly button";
        // Surgical skin-marker violet; landmarks and guide stay a quiet grey-white.
        public static readonly Color InkColor = new Color(.23f, .04f, .38f, 1), MarkColor = new Color(.93f, .93f, .95f, .9f);
        // Metres. Lift keeps every overlay just outside the surface it lies on, so nothing depth-fights the skin.
        public const float Lift = .0008f, InkWidth = .0022f;
        const float FadeSeconds = .8f, GuideWidth = .0014f, Dash = .004f, Gap = .003f, DotRadius = .0032f, LabelHeight = .012f;
        AnatomyExerciseBinding exercise;
        OpenBodyInteraction interaction;
        OpenWoundView wound;
        NativeVolumeSimulation volume;
        SurgeryTriggerHint hint;
        Transform head, instructionAnchor;
        Collider skin;
        Func<bool> gate;
        InstrumentBehaviour marker;
        GameObject root, inkRoot;
        LineRenderer ink, liveInk;
        Material markMaterial, inkMaterial;
        Mesh dotMesh, guideMesh;
        readonly List<TextMeshPro> labels = new List<TextMeshPro>();
        readonly List<Vector3> inkPoints = new List<Vector3>();
        string milestoneId = "", farFeedback = "", pendingReason;
        Transform pendingAnchor;
        BodyPredicate[] milestone = Array.Empty<BodyPredicate>();
        float alpha;
        bool inkOnPatient, drawing;

        public GameObject Root => root;
        public bool Showing => root && root.activeSelf && alpha > 0;
        public bool Accepted => exercise && !string.IsNullOrEmpty(milestoneId) && Contains(exercise.CompletedMilestones, milestoneId);
        public LineRenderer Ink => ink;
        public LineRenderer LiveInk => liveInk;
        public Transform HipDot { get; private set; }
        public Transform NavelDot { get; private set; }
        public Transform GuideLine { get; private set; }
        public IReadOnlyList<TextMeshPro> Labels => labels;
        public string LastReason { get; private set; } = "";

        public void Initialize(NativeCaseSession session, OpenBodyInteraction source, OpenWoundView woundView, SurgeryTriggerHint hintLine,
            Transform rightAsis, Transform umbilicus, Func<bool> canShow)
        {
            Unsubscribe(); Dispose();
            exercise = session.exercise; interaction = source; wound = woundView; hint = hintLine; gate = canShow;
            volume = session.GetComponent<NativeVolumeSimulation>();
            head = session.workbench && session.workbench.headCamera ? session.workbench.headCamera.transform : null;
            marker = null;
            foreach (var tool in session.workbench.tools ?? Array.Empty<InstrumentBehaviour>())
                if (tool && tool.gameObject.activeInHierarchy && BodyState.ToolVerbs.TryGetValue(tool.instrumentId, out var verbs) && verbs[0] == "mark") { marker = tool; break; }
            bool passthrough = session.presentation && session.presentation.passthrough;
            skin = null;
            // The visible VR patient's physics copy of its skin; AR has no virtual skin and draws on the registered wall plane.
            if (!passthrough && session.presentation && session.presentation.virtualMannequin)
                foreach (var candidate in session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true))
                    if (candidate.enabled && candidate.sharedMesh) { skin = candidate; break; }
            var plan = exercise && exercise.SelectedCase?.procedure?.openBody != null ? exercise.SelectedCase.procedure.openBody : null;
            milestoneId = ""; milestone = Array.Empty<BodyPredicate>(); farFeedback = ""; LastReason = ""; pendingReason = null;
            foreach (var candidate in plan?.milestones ?? Array.Empty<BodyMilestone>())
                if (Array.Exists(candidate.predicates ?? Array.Empty<BodyPredicate>(), p => p.tissueId == "skin" && p.fact == "marked")) { milestoneId = candidate.id; milestone = candidate.predicates; break; }
            foreach (var rule in plan?.guardrails ?? Array.Empty<BodyGuardrail>())
                if (rule.tissueId == "skin" && rule.verb == "mark" && rule.eventPredicate?.fact == "distanceMm") { farFeedback = rule.feedback ?? ""; break; }
            if (!wound || !interaction || milestoneId == "") return;
            Build(session, passthrough, rightAsis, umbilicus);
            interaction.MarkerDrawing += Drawing; interaction.Submitted += Submitted;
            alpha = 0; inkOnPatient = true; drawing = false;
        }

        void Build(NativeCaseSession session, bool passthrough, Transform rightAsis, Transform umbilicus)
        {
            var brand = ScalpalBrand.Active;
            markMaterial = new Material(brand.accent) { name = "MarkingGuideMarks", mainTexture = Texture2D.whiteTexture, color = MarkColor };
            inkMaterial = new Material(brand.accent) { name = "SurgicalMarkerInk", mainTexture = Texture2D.whiteTexture, color = InkColor };
            inkMaterial.renderQueue = markMaterial.renderQueue + 1; // ink over the dashes it follows
            root = new GameObject("McBurneyMarkingGuide"); root.transform.SetParent(wound.transform, false);
            // Dashed guide: the registered reference line the mark is scored against, laid on the visible skin.
            Vector3 start = interaction.ReferenceStart, end = interaction.ReferenceEnd;
            var guide = new GameObject("McBurneyGuideLine", typeof(MeshFilter), typeof(MeshRenderer)); guide.transform.SetParent(root.transform, false);
            guideMesh = DashedLine(start, end); guide.GetComponent<MeshFilter>().sharedMesh = guideMesh;
            Quiet(guide.GetComponent<MeshRenderer>(), markMaterial); GuideLine = guide.transform;
            instructionAnchor = new GameObject("MarkingInstructionAnchor").transform; instructionAnchor.SetParent(root.transform, false);
            // Beside the guide on the side away from the belly button, so the line never sits on a landmark.
            Vector3 away = Vector3.Cross(end - start, Vector3.forward).normalized * .045f;
            instructionAnchor.localPosition = Surface((start + end) * .5f + away, true) + Vector3.back * .04f;
            // Landmarks come from registration when bound; the virtual patient shows its authored proxies; otherwise none.
            bool registered = rightAsis && umbilicus;
            if (registered || !passthrough)
            {
                Vector3 hip = registered ? rightAsis.position : session.patientFrame.TransformPoint(OpenSurgerySession.AuthoredRightAsis);
                Vector3 navel = registered ? umbilicus.position : session.patientFrame.position;
                dotMesh = Disc();
                HipDot = Landmark("HipBoneLandmark", HipLabel, hip, brand);
                NavelDot = Landmark("BellyButtonLandmark", NavelLabel, navel, brand);
            }
            inkRoot = new GameObject("SurgicalMarkerInk"); inkRoot.transform.SetParent(wound.transform, false);
            ink = InkLine("CommittedInk"); liveInk = InkLine("LiveInk");
            root.SetActive(false);
        }

        Transform Landmark(string name, string text, Vector3 world, ScalpalBrand brand)
        {
            Vector3 local = wound.transform.InverseTransformPoint(world);
            // A registered landmark already lies on the participant; the virtual patient's proxy is laid onto its skin.
            Vector3 point = skin ? wound.transform.TransformPoint(Surface(local, true)) : world;
            var dot = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)); dot.transform.SetParent(root.transform, false);
            dot.transform.SetPositionAndRotation(point, wound.transform.rotation);
            dot.GetComponent<MeshFilter>().sharedMesh = dotMesh; Quiet(dot.GetComponent<MeshRenderer>(), markMaterial);
            var label = brand.Text(dot.transform, name + "Label", text, ScalpalTextRole.Label, Vector3.zero, LabelHeight, .2f, .03f, TextAnchor.LowerCenter);
            label.transform.position = point - wound.transform.forward * .022f;
            label.color = MarkColor; labels.Add(label);
            return dot.transform;
        }

        LineRenderer InkLine(string name)
        {
            var go = new GameObject(name); go.transform.SetParent(inkRoot.transform, false);
            var line = go.AddComponent<LineRenderer>(); line.useWorldSpace = false; line.positionCount = 0;
            // Flat on the skin (the wound's +Z is the skin normal), not a camera-facing ribbon.
            line.alignment = LineAlignment.TransformZ; line.startWidth = line.endWidth = InkWidth;
            line.numCapVertices = 4; line.numCornerVertices = 2; Quiet(line, inkMaterial); line.enabled = false;
            return line;
        }

        static void Quiet(Renderer renderer, Material material)
        {
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
        }

        // Wound-local point on the visible skin, lifted just outside it. The intact VR patient's skin is the physics
        // copy of the rendered mannequin; otherwise (opened wall, AR) the skin is the registered wall plane (z = 0).
        Vector3 Surface(Vector3 local, bool onPatient)
        {
            local.z = 0;
            if (onPatient && skin && skin.enabled)
            {
                Vector3 inward = wound.transform.forward, world = wound.transform.TransformPoint(local);
                bool backfaces = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true; // a few mannequin triangles near the midline are wound inward
                try { if (skin.Raycast(new Ray(world - inward * .25f, inward), out var hit, .5f)) local.z = wound.transform.InverseTransformPoint(hit.point).z; }
                finally { Physics.queriesHitBackfaces = backfaces; }
            }
            local.z -= Lift;
            return local;
        }

        Mesh DashedLine(Vector3 start, Vector3 end)
        {
            Vector3 axis = end - start; axis.z = 0; float length = axis.magnitude; axis /= Mathf.Max(length, 1e-6f);
            Vector3 side = new Vector3(-axis.y, axis.x, 0) * (GuideWidth * .5f);
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (float at = 0; at < length - .0005f; at += Dash + Gap)
            {
                float to = Mathf.Min(at + Dash, length);
                Vector3 a = start + axis * at, b = start + axis * to;
                int first = vertices.Count;
                vertices.Add(Surface(a - side, true)); vertices.Add(Surface(a + side, true)); vertices.Add(Surface(b + side, true)); vertices.Add(Surface(b - side, true));
                triangles.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
            }
            var mesh = new Mesh { name = "McBurneyDashedGuide" }; mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh Disc()
        {
            const int segments = 20;
            var vertices = new Vector3[segments + 1]; var triangles = new int[segments * 3];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2 / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * DotRadius;
                triangles[i * 3] = 0; triangles[i * 3 + 1] = i + 1; triangles[i * 3 + 2] = i == segments - 1 ? 1 : i + 2;
            }
            var mesh = new Mesh { name = "LandmarkDot", vertices = vertices, triangles = triangles }; mesh.RecalculateBounds();
            return mesh;
        }

        // Accepted (shared engine) marks only: the stroke the body recorded, as the learner saw it drawn.
        public void SetInk(IReadOnlyList<Vector3> points)
        {
            inkPoints.Clear();
            if (points == null || points.Count < 2) { if (ink) { ink.positionCount = 0; ink.enabled = false; } return; }
            foreach (var point in points) if (!OpenSurgeryStroke.Finite(point)) return;
            for (int i = 0; i < points.Count; i++)
            {
                // A two-point premark still follows the curved skin.
                if (i > 0) { int pieces = Mathf.CeilToInt(Vector3.Distance(points[i - 1], points[i]) / .002f); for (int j = 1; j < pieces; j++) inkPoints.Add(Vector3.Lerp(points[i - 1], points[i], j / (float)pieces)); }
                inkPoints.Add(points[i]);
            }
            Project(ink, inkPoints);
        }

        void Drawing(InstrumentBehaviour tool, IReadOnlyList<Vector3> points)
        {
            if (!liveInk || tool != marker) return;
            drawing = points.Count > 0;
            if (points.Count < liveInk.positionCount || points.Count == 0) liveInk.positionCount = 0;
            for (int i = liveInk.positionCount; i < points.Count; i++) { liveInk.positionCount = i + 1; liveInk.SetPosition(i, Surface(points[i], inkOnPatient)); }
            liveInk.enabled = points.Count >= 2;
        }

        void Project(LineRenderer line, List<Vector3> points)
        {
            if (!line) return;
            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, Surface(points[i], inkOnPatient));
        }

        // One short reason when a recorded mark leaves the marking milestone unmet; the far case uses the case's own
        // guardrail feedback. Thresholds are the case's predicates, read rather than restated.
        void Submitted(BodyRecord record, InstrumentBehaviour tool)
        {
            if (record?.action == null || record.action.verb != "mark" || record.action.tissueId != "skin" || Accepted) return;
            var body = exercise.Body; if (body == null) return;
            foreach (var predicate in milestone)
            {
                if (body.Test(predicate)) continue;
                // Shown after this frame's tool updates, so lifting the trigger on the skin cannot cover it.
                LastReason = pendingReason = Reason(predicate, farFeedback);
                pendingAnchor = tool && tool.actionPoint ? tool.actionPoint : instructionAnchor;
                return;
            }
        }

        public static string Reason(BodyPredicate predicate, string farFeedback)
        {
            switch (predicate.fact)
            {
                case "markErrorMm": return string.IsNullOrEmpty(farFeedback) ? "Too far from the dotted line." : farFeedback;
                case "markLengthMm": return predicate.op == "gte" ? "Too short. Draw the whole dotted line." : "Too long. Stop at the end of the line.";
                case "markAngleDegrees": return "Wrong angle. Follow the dotted line.";
                default: return "Mark not accepted. Draw along the dotted line.";
            }
        }

        void LateUpdate() => Refresh(Time.unscaledDeltaTime);

        // Owner-order refresh (also driven by Editor validations after the wall solver's step).
        public void Refresh(float seconds)
        {
            if (!root || !wound || exercise == null) return;
            var body = exercise.Body;
            bool ready = body != null && gate != null && gate();
            bool opened = body != null && body.Get("skin", "opened") > 0;
            // Until the skin is opened there is nothing under it to show: the patient's skin stays whole.
            bool concealed = body != null && !opened;
            wound.Concealed = concealed;
            if (concealed && volume && volume.Wall) { var wall = volume.Wall.GetComponent<MeshRenderer>(); if (wall) wall.enabled = false; }
            if (inkOnPatient != concealed) { inkOnPatient = concealed; Project(ink, inkPoints); liveInk.positionCount = 0; }
            inkRoot.SetActive(ready);
            ink.enabled = ink.positionCount >= 2 && body != null && body.Get("skin", "marked") > 0 && body.Get("skin", "closed") == 0;
            bool wanted = ready && !opened && !Accepted;
            if (wanted) alpha = 1;
            else alpha = ready ? Mathf.Max(0, alpha - Mathf.Max(0, seconds) / FadeSeconds) : 0;
            root.SetActive(alpha > 0);
            if (alpha > 0) Fade();
            if (pendingReason != null && hint && ready) hint.Say(pendingAnchor ? pendingAnchor : instructionAnchor, pendingReason, null, head, 4, .6f);
            pendingReason = null;
            Instruct(wanted);
        }

        void Fade()
        {
            var color = MarkColor; color.a *= alpha; markMaterial.color = color;
            foreach (var label in labels)
            {
                label.color = color;
                if (head) label.transform.rotation = Quaternion.LookRotation(label.transform.position - head.position, -wound.transform.forward);
            }
        }

        // The hint line carries the step: above the guide until the marker is picked up, then at its tip; never
        // over the tip while it draws, and never over a trigger reminder or a reason that is still showing.
        void Instruct(bool wanted)
        {
            if (!hint) return;
            string shown = hint.Text;
            if (!wanted || drawing) { if (shown == Instruction) hint.Hide(); return; }
            if (shown != "" && shown != Instruction) return;
            hint.Say(marker && marker.Held && marker.actionPoint ? marker.actionPoint : instructionAnchor, Instruction, null, head, .5f, .5f);
        }

        static bool Contains(IReadOnlyCollection<string> values, string value)
        {
            if (values == null) return false;
            foreach (var item in values) if (item == value) return true;
            return false;
        }

        void Unsubscribe() { if (interaction) { interaction.MarkerDrawing -= Drawing; interaction.Submitted -= Submitted; } }
        void Dispose()
        {
            if (root) Release(root); if (inkRoot) Release(inkRoot);
            Release(markMaterial); Release(inkMaterial); Release(dotMesh); Release(guideMesh);
            root = inkRoot = null; ink = liveInk = null; markMaterial = inkMaterial = null; dotMesh = guideMesh = null;
            HipDot = NavelDot = GuideLine = null; instructionAnchor = null; labels.Clear(); inkPoints.Clear();
        }
        static void Release(UnityEngine.Object value)
        {
            if (!value) return;
#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.IsPersistent(value)) return;
#endif
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
        void OnDestroy() { Unsubscribe(); Dispose(); }
    }
}
