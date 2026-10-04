using System.Collections.Generic;
using Scalpal.Anatomy.Tissue;
using Scalpal.Quest;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Presentation of the already recorded assistant closure. Retained organs/instruments
    // are concealed, not teleported, healed or removed from the scored physics model.
    // The visible seam is a teaching approximation, not a suturing mechanics simulation.
    [DisallowMultipleComponent, DefaultExecutionOrder(500)]
    public sealed class SurgicalClosureAppearance : MonoBehaviour
    {
        NativeCaseSession session;
        OpenBodyInteraction input;
        Transform wound;
        GameObject view;
        Material seamMaterial, stitchMaterial;
        readonly Dictionary<Renderer, bool> hidden = new Dictionary<Renderer, bool>();
        bool closed;
        public bool ClosureVisible => view && view.activeInHierarchy;
        public int ConcealedRendererCount => hidden.Count;

        public void Initialize(NativeCaseSession owner, OpenBodyInteraction interaction, Transform field)
        {
            Dispose(); session = owner; input = interaction; wound = field;
        }
        public void Refresh()
        {
            if (!session || !input || !wound || !session.exercise) return;
            bool wanted = session.exercise.Body != null && session.exercise.Body.Get("skin", "closed") > 0;
            if (wanted && !closed)
            {
                foreach (var group in input.Mobility)
                    foreach (var part in group.Parts)
                        if (part) foreach (var renderer in part.GetComponentsInChildren<Renderer>(true)) Conceal(renderer);
                foreach (var tool in session.workbench.tools)
                {
                    var latch = tool ? tool.GetComponent<SurgeryInstrumentLatch>() : null;
                    if (latch && latch.Attached && !tool.Held)
                        foreach (var renderer in tool.GetComponentsInChildren<Renderer>(true)) Conceal(renderer);
                }
                BuildSeam();
            }
            if (!wanted && closed) RestoreDraws();
            closed = wanted;
            // Other presentation components may refresh after the anatomy's visibility gate.
            if (closed) foreach (var pair in hidden) if (pair.Key) pair.Key.forceRenderingOff = true;
            if (view) view.SetActive(closed && session.anatomy && session.anatomy.RegistrationValid && session.anatomy.CanDisplay);
        }
        void Conceal(Renderer renderer)
        {
            if (!renderer || hidden.ContainsKey(renderer)) return;
            hidden.Add(renderer, renderer.forceRenderingOff); renderer.forceRenderingOff = true;
        }
        void RestoreDraws()
        {
            foreach (var pair in hidden) if (pair.Key) pair.Key.forceRenderingOff = pair.Value;
            hidden.Clear();
        }
        void BuildSeam()
        {
            if (view) Release(view);
            view = new GameObject("AssistedClosure_SeamAndInterruptedStitches"); view.transform.SetParent(wound, false);
            seamMaterial = seamMaterial ? seamMaterial : TissueRuntimeMaterial.Create("ClosedIncisionSeam", new Color(.38f, .19f, .17f));
            stitchMaterial = stitchMaterial ? stitchMaterial : TissueRuntimeMaterial.Create("TeachingSkinSutures", new Color(.12f, .18f, .25f));
            seamMaterial.SetFloat("_Glossiness", .28f); stitchMaterial.SetFloat("_Glossiness", .36f);
            Vector3 start = input.ReferenceStart, end = input.ReferenceEnd;
            const int samples = 32;
            var points = new Vector3[samples + 1];
            for (int i = 0; i <= samples; i++) points[i] = OnSkin(Vector3.Lerp(start, end, i / (float)samples));
            Line("ApposedIncision", points, .0008f, seamMaterial);
            Vector3 side = Vector3.Cross(Vector3.forward, (end - start).normalized);
            for (int i = 1; i <= 6; i++)
            {
                Vector3 at = Vector3.Lerp(start, end, i / 7f);
                var stitch = new[] { OnSkin(at - side * .003f), OnSkin(at) - Vector3.forward * .0005f, OnSkin(at + side * .003f) };
                Line("InterruptedSkinStitch" + i, stitch, .00045f, stitchMaterial);
            }
        }
        Vector3 OnSkin(Vector3 local)
        {
            local.z = -.0008f;
            if (!session.presentation || session.presentation.passthrough || !session.presentation.virtualMannequin) return local;
            Vector3 origin = wound.TransformPoint(new Vector3(local.x, local.y, 0));
            bool previous = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;
            try
            {
                foreach (var skin in session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true))
                    if (skin.enabled && skin.Raycast(new Ray(origin - wound.forward * .25f, wound.forward), out var hit, .5f))
                    { local.z = wound.InverseTransformPoint(hit.point).z - .0008f; break; }
            }
            finally { Physics.queriesHitBackfaces = previous; }
            return local;
        }
        void Line(string name, Vector3[] points, float width, Material material)
        {
            var go = new GameObject(name); go.transform.SetParent(view.transform, false);
            var line = go.AddComponent<LineRenderer>(); line.useWorldSpace = false;
            line.positionCount = points.Length; line.SetPositions(points); line.startWidth = line.endWidth = width;
            line.numCapVertices = 3; line.numCornerVertices = 3; line.sharedMaterial = material;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        void LateUpdate() => Refresh();
        public void Dispose()
        {
            RestoreDraws(); closed = false;
            if (view) Release(view); if (seamMaterial) Release(seamMaterial); if (stitchMaterial) Release(stitchMaterial);
            view = null; seamMaterial = stitchMaterial = null; session = null; input = null; wound = null;
        }
        void OnDisable() { RestoreDraws(); closed = false; if (view) view.SetActive(false); }
        void OnDestroy() => Dispose();
        static void Release(Object item) { if (Application.isPlaying) Destroy(item); else DestroyImmediate(item); }
    }
}
