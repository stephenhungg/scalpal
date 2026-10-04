using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // Local mechanical feedback only. NativeProcedureInput remains the sole scored path.
    [DefaultExecutionOrder(110)]
    public sealed class NativeTissueSimulation : MonoBehaviour
    {
        readonly List<DeformableTissue> tissues = new List<DeformableTissue>();
        readonly TissueContactSolver contactSolver = new TissueContactSolver();
        NativeWorkbench workbench;
        AnatomyController anatomy;
        Func<bool> ready;
        DeformableTissue grabbed;
        InstrumentBehaviour tool;
        Vector3 localOffset;
        float accumulator, surfaceClock;
        bool wasReady;
        double nextTiming;
        float peakSurfaceMs;
        int surfaceCommits;
        const float StepSeconds = 1f / 90f;
        public int TissueCount => tissues.Count;
        public void Initialize(AnatomyController controller, NativeWorkbench rig, Func<bool> canInteract)
        {
            ResetTissues(); tissues.Clear(); anatomy = controller; workbench = rig; ready = canInteract;
            Bind("appendix", TissuePreset.Bowel); Bind("mesoappendix", TissuePreset.Mesentery); Bind("appendicular_artery", TissuePreset.Artery);
            contactSolver.Initialize(tissues,true);
            if(contactSolver.SupportedBodies != tissues.Count) Debug.LogWarning("SCALPAL_TISSUE_CONTACT_UNAVAILABLE " + contactSolver.Status);
        }
        void Bind(string id, TissuePreset preset)
        {
            if (!anatomy || !anatomy.TryGetPart(id, out var part)) return;
            Vector3 meshScale=part.transform.lossyScale,frameScale=anatomy.transform.lossyScale;
            float factor=meshScale.x/frameScale.x;
            if(!TissueCage.Finite(meshScale)||!TissueCage.Finite(frameScale)||!(factor>0)||
                Mathf.Abs(meshScale.y/frameScale.y-factor)>factor*.001f||Mathf.Abs(meshScale.z/frameScale.z-factor)>factor*.001f)return;
            var tissue = part.GetComponent<DeformableTissue>() ?? part.gameObject.AddComponent<DeformableTissue>();
            if (tissue.Initialize(preset,factor)) tissues.Add(tissue);
            else { Debug.LogWarning("SCALPAL_TISSUE_UNAVAILABLE id=" + id); Destroy(tissue); }
        }
        void LateUpdate() => Simulate(Time.deltaTime);
        public void Simulate(float seconds)
        {
            bool valid = isActiveAndEnabled && ready != null && ready() && anatomy && anatomy.RegistrationValid;
            if (!valid)
            {
                if (wasReady || grabbed) ResetTissues();
                wasReady = false; return;
            }
            wasReady = true;
            if (grabbed && (!ValidTool(tool) || !grabbed.GetComponent<AnatomyPart>().IsVisible)) Release();
            if (!grabbed) FindGrasp();
            accumulator = Mathf.Min(accumulator + Mathf.Clamp(seconds, 0, .05f), .05f);
            int steps = 0;
            while (accumulator >= StepSeconds && steps++ < 4)
            {
                foreach (var tissue in tissues)
                    tissue.Step(StepSeconds, tissue == grabbed ? tissue.ToMeters(tissue.transform.InverseTransformPoint(tool.actionPoint.position)) + localOffset : Vector3.zero);
                accumulator -= StepSeconds;
            }
            surfaceClock += Mathf.Clamp(seconds, 0, .05f);
            if (surfaceClock >= 1f/30f)
            {
                double begin = Time.realtimeSinceStartupAsDouble;
                contactSolver.Solve(true);
                foreach (var tissue in tissues) tissue.CommitSurface();
                peakSurfaceMs = Mathf.Max(peakSurfaceMs, (float)((Time.realtimeSinceStartupAsDouble - begin) * 1000));
                surfaceCommits++; surfaceClock %= 1f/30f;
            }
            if (Application.isPlaying && Time.realtimeSinceStartupAsDouble >= nextTiming)
            {
                Debug.Log($"SCALPAL_NATIVE_TISSUE_TIMING targets={tissues.Count} surfaceCommits={surfaceCommits} peakSurfaceMs={peakSurfaceMs:F2} contactSupported={contactSolver.SupportedBodies} contactPairs={contactSolver.AppliedPairs} excessResidualMm={contactSolver.MaximumResidualPenetrationMeters*1000:F2} authoredOverlapMm={contactSolver.MaximumAuthoredOverlapMeters*1000:F2} samplingGapMm={contactSolver.MaximumSamplingGapMeters*1000:F2}");
                nextTiming = Time.realtimeSinceStartupAsDouble + 5; peakSurfaceMs=0; surfaceCommits=0;
            }
        }
        bool ValidTool(InstrumentBehaviour item) => workbench && Array.IndexOf(workbench.tools ?? Array.Empty<InstrumentBehaviour>(), item) >= 0 && item && item.isActiveAndEnabled && item.Held && item.TrackingValid &&
            item.Activation >= .7f && item.actionPoint && (item.action == InstrumentAction.Grasp || item.action == InstrumentAction.Retrieve);
        void FindGrasp()
        {
            if (!workbench) return;
            float nearest = float.PositiveInfinity;
            foreach (var candidate in workbench.tools ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!ValidTool(candidate)) continue;
                foreach (var tissue in tissues)
                {
                    var part = tissue.GetComponent<AnatomyPart>(); var collider = tissue.GetComponent<MeshCollider>();
                    if (!part.IsVisible || !part.HasVisibleGeometry || !collider.enabled) continue;
                    // Require actual tip penetration, same as the scored adapter. Sphere proximity alone is insufficient.
                    bool overlap = false;
                    foreach (var tip in candidate.GetComponentsInChildren<InstrumentTipContact>())
                    {
                        var shape = tip.GetComponent<Collider>();
                        if (tip.GetComponentInParent<InstrumentBehaviour>() != candidate || !tip.isActiveAndEnabled || !shape || !shape.enabled || !shape.isTrigger ||
                            Vector3.Distance(tip.transform.position,candidate.actionPoint.position) > candidate.contactRadius) continue;
                        overlap |= Physics.ComputePenetration(shape,shape.transform.position,shape.transform.rotation,collider,collider.transform.position,collider.transform.rotation,out _,out _);
                    }
                    float distance = (collider.bounds.center - candidate.actionPoint.position).sqrMagnitude;
                    if (overlap && distance < nearest) { nearest=distance; grabbed=tissue; tool=candidate; }
                }
            }
            if (!grabbed) return;
            Vector3 local = grabbed.ToMeters(grabbed.transform.InverseTransformPoint(tool.actionPoint.position));
            if (!grabbed.Cage.BeginHandle(local)) { Release(); return; }
            localOffset = grabbed.Cage.HandlePosition - local; // No snap to a distant cage corner on acquisition.
        }
        void Release()
        {
            if (grabbed) grabbed.Cage.ReleaseHandle();
            grabbed=null; tool=null;
        }
        public void ResetTissues()
        {
            Release(); foreach (var tissue in tissues) if (tissue) tissue.ResetTissue();
            accumulator=surfaceClock=0; wasReady=false;
        }
        void OnDisable() => ResetTissues();
    }
}
