using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Engine;
using Scalpal.Quest;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Visible blood flow from the sim's active bleeds: one pooled ParticleSystem of drops that collide with the
    // patient, table and floor and leave a capped ring of splats where they land. Sources are read every frame
    // from facts the sim already owns: perfused tissues the body reports bleeding (their measured or authored
    // rate, at their last cut) and regions the interaction reports injured and not controlled (the coach's rate).
    // A high-rate source spurts in time with the simulated heart rate, a low-rate one wells up. Presentation
    // only: nothing here is scored or reported. AR shows none of it, since there is no virtual body there.
    public sealed class SurgeryBlood : MonoBehaviour
    {
        public const int MaxParticles = 300, MaxSplats = 64, SplatsPerFrame = 6;
        // A visible drop's volume; above the per-source drop cap the same volume leaves in fewer, larger drops.
        public const float DropMilliliters = .05f, MaxDropsPerSecond = 120, ArterialMlPerMin = 60, AuthoredHeartRate = 72;
        public static readonly Color Blood = new Color32(0x8a, 0x03, 0x03, 0xff);
        // Mirror of rawBleedMlPerMin in services/preop/src/patient-condition.ts REGIONS, used only until a fresh
        // coach condition names the region (offline, or before the first sample arrives).
        static readonly string[] Regions = { "head", "neck", "chest", "left_arm", "right_arm", "left_leg", "right_leg" };
        static readonly float[] AuthoredRegionMlPerMin = { 0, 300, 150, 20, 20, 20, 20 };
        public struct Flow { public string id; public Vector3 point, normal; public float rateMlPerMin; public bool arterial; }
        struct Splat { public Vector3 point, normal; public float radius; public int seed; }
        readonly List<Flow> flows = new List<Flow>();
        readonly Dictionary<string, float> carry = new Dictionary<string, float>();
        readonly Dictionary<string, long> emitted = new Dictionary<string, long>();
        readonly Dictionary<string, Vector3> cutPoints = new Dictionary<string, Vector3>();
        readonly Dictionary<string, Vector3> injuryPoints = new Dictionary<string, Vector3>();
        readonly List<ParticleCollisionEvent> hits = new List<ParticleCollisionEvent>();
        readonly Splat[] splats = new Splat[MaxSplats];
        ParticleSystem drops;
        Mesh dropMesh, splatMesh;
        MeshRenderer splatRenderer;
        Material material;
        OpenBodyInteraction input;
        PatientIncisions incisions;
        AnatomyExerciseBinding exercise;
        NativePatientMonitor monitor;
        Transform torso, wound;
        Func<bool> virtualBody;
        BodyState cachedBody;
        int cachedLog, splatHead, splatCount, splatBudget;
        bool splatsDirty;
        double clock;
        public IReadOnlyList<Flow> Flows => flows;
        public long Emitted { get; private set; }
        public long EmittedFor(string id) => emitted.TryGetValue(id, out var value) ? value : 0;
        public int SplatCount => splatCount;
        public int ParticleCount => drops ? drops.particleCount : 0;
        public ParticleSystem Drops => drops;
        public Mesh SplatMesh => splatMesh;
        public bool Visible => torso && (virtualBody == null || virtualBody());

        public void Initialize(OpenBodyInteraction interaction, PatientIncisions cuts, AnatomyExerciseBinding binding, Transform torsoFrame,
            Transform woundFrame, Func<bool> showsVirtualBody, NativePatientMonitor patientMonitor)
        {
            if (input) input.RegionInjured -= Injured;
            input = interaction; incisions = cuts; exercise = binding; torso = torsoFrame; wound = woundFrame;
            virtualBody = showsVirtualBody; monitor = patientMonitor;
            if (input) input.RegionInjured += Injured;
            Build(); Clear();
        }
        // A new attempt starts with no blood anywhere.
        public void Clear()
        {
            flows.Clear(); carry.Clear(); emitted.Clear(); cutPoints.Clear(); injuryPoints.Clear();
            Emitted = 0; clock = 0; cachedBody = null; cachedLog = 0; splatHead = splatCount = 0; splatsDirty = true;
            if (drops) drops.Clear();
            RebuildSplats();
        }
        // Where the blade was when the region was injured, in case it never reached the visible skin there.
        void Injured(string region, Scalpal.Instruments.InstrumentBehaviour tool, bool controlled)
        {
            if (!controlled && tool && tool.actionPoint && torso) injuryPoints[region] = torso.InverseTransformPoint(tool.actionPoint.position);
        }

        public void Simulate(float seconds)
        {
            flows.Clear(); splatBudget = SplatsPerFrame;
            bool visible = Visible;
            if (splatRenderer) splatRenderer.enabled = visible && splatCount > 0;
            if (!visible) { if (drops && drops.particleCount > 0) drops.Clear(); return; }
            if (splatsDirty) RebuildSplats();
            if (!input || !input.Ready || exercise?.Body == null || !float.IsFinite(seconds) || seconds <= 0 || seconds > .1f) return;
            Collect(exercise.Body);
            float heartRate = HeartRate();
            foreach (var flow in flows) Emit(flow, seconds, heartRate);
            clock += seconds;
        }
        void Collect(BodyState body)
        {
            if (body != cachedBody) { cachedBody = body; cachedLog = 0; cutPoints.Clear(); }
            for (; cachedLog < body.Log.Count; cachedLog++)
            {
                var action = body.Log[cachedLog].action;
                if (action.verb == "cut") cutPoints[action.tissueId] = new Vector3((float)action.position.x, (float)action.position.y, (float)action.position.z);
            }
            Vector3 outward = wound ? torso.InverseTransformDirection(-wound.forward) : Vector3.up; // wound +Z points inward
            foreach (var tissue in body.Tissues)
            {
                if (!tissue.perfused || body.Get(tissue.id, "bleeding") <= 0 || !cutPoints.TryGetValue(tissue.id, out var point)) continue;
                double perSecond = body.Get(tissue.id, "fluidDriven") > 0 ? body.Get(tissue.id, "measuredFlowMlPerSecond") : tissue.flowMlPerSecond;
                Add(tissue.id, point, outward, (float)(perSecond * 60));
            }
            for (int i = 0; i < Regions.Length; i++)
            {
                string region = Regions[i];
                if (!input.IsRegionInjured(region)) continue;
                float rate = RegionRate(i);
                if (incisions && incisions.TryRegionPoint(region, out var point, out var normal)) Add(region, point, normal, rate);
                else if (injuryPoints.TryGetValue(region, out point)) Add(region, point, Vector3.up, rate);
            }
        }
        void Add(string id, Vector3 point, Vector3 normal, float rateMlPerMin)
        {
            if (!(rateMlPerMin > 0) || float.IsInfinity(rateMlPerMin)) return;
            flows.Add(new Flow { id = id, point = point, normal = normal.normalized, rateMlPerMin = rateMlPerMin, arterial = rateMlPerMin >= ArterialMlPerMin });
        }
        // The server's simulated heart rate when fresh (0 once flatlined), else the authored baseline.
        float HeartRate()
        {
            var condition = monitor ? monitor.Condition : null;
            if (condition?.vitals == null) return AuthoredHeartRate;
            return monitor.Flatline ? 0 : condition.vitals.hr;
        }
        float RegionRate(int index)
        {
            var condition = monitor ? monitor.Condition : null;
            if (condition?.regions != null)
                foreach (var region in condition.regions)
                    if (region != null && region.region == Regions[index]) return region.bleeding ? region.rawBleedMlPerMin : 0;
            return AuthoredRegionMlPerMin[index];
        }
        void Emit(Flow flow, float seconds, float heartRate)
        {
            float perSecond = flow.rateMlPerMin / 60f / DropMilliliters, size = .0035f;
            if (perSecond > MaxDropsPerSecond) { size *= Mathf.Pow(perSecond / MaxDropsPerSecond, 1 / 3f); perSecond = MaxDropsPerSecond; }
            // An artery spurts in the first 30% of each beat; the pulse averages to 1, so the mean rate is unchanged.
            float squeeze = 0, pulse = 1;
            if (flow.arterial && heartRate > 0)
            {
                double phase = clock * heartRate / 60; phase -= Math.Floor(phase);
                squeeze = phase < .3 ? Mathf.Pow(Mathf.Sin(Mathf.PI * (float)phase / .3f), 2) : 0;
                pulse = squeeze / .15f;
            }
            carry.TryGetValue(flow.id, out float owed); owed += perSecond * pulse * seconds;
            int count = Mathf.FloorToInt(owed); carry[flow.id] = owed - count;
            if (count <= 0) return;
            float speed = flow.arterial && heartRate > 0 ? Mathf.Lerp(.1f, .5f + .6f * Mathf.Clamp01(flow.rateMlPerMin / 300f), squeeze) : .02f;
            Vector3 origin = torso.TransformPoint(flow.point), normal = torso.TransformDirection(flow.normal).normalized;
            Vector3 lift = flow.arterial ? Vector3.up * .15f : Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                // Drops start just outside the surface so they do not collide with the skin they leave.
                var parameters = new ParticleSystem.EmitParams
                {
                    position = origin + normal * .003f + UnityEngine.Random.insideUnitSphere * .0015f,
                    velocity = (normal + lift + UnityEngine.Random.insideUnitSphere * .25f).normalized * speed * UnityEngine.Random.Range(.85f, 1.15f),
                    startSize = size * UnityEngine.Random.Range(.8f, 1.2f), startLifetime = 1.6f, startColor = Blood
                };
                drops.Emit(parameters, 1);
            }
            Emitted += count; emitted.TryGetValue(flow.id, out long total); emitted[flow.id] = total + count;
        }

        // Drops die where they land and leave a splat there; nearby landings grow one splat into a small pool.
        void OnParticleCollision(GameObject other)
        {
            if (!drops) return;
            int count = drops.GetCollisionEvents(other, hits);
            for (int i = 0; i < count && splatBudget > 0; i++, splatBudget--) AddSplat(hits[i].intersection, hits[i].normal);
        }
        public bool AddSplat(Vector3 point, Vector3 normal)
        {
            if (!Visible || !OpenSurgeryStroke.Finite(point) || !OpenSurgeryStroke.Finite(normal) || normal.sqrMagnitude < 1e-6f) return false;
            normal.Normalize();
            for (int i = 0; i < splatCount; i++)
            {
                ref var splat = ref splats[i];
                if ((splat.point - point).sqrMagnitude > splat.radius * splat.radius * .64f || Vector3.Dot(splat.normal, normal) < .7f) continue;
                splat.radius = Mathf.Min(.03f, splat.radius + .0012f); splatsDirty = true; return true;
            }
            splats[splatHead] = new Splat { point = point, normal = normal, radius = UnityEngine.Random.Range(.004f, .009f), seed = UnityEngine.Random.Range(0, 1000) };
            splatHead = (splatHead + 1) % MaxSplats; splatCount = Mathf.Min(splatCount + 1, MaxSplats); splatsDirty = true;
            return true;
        }
        // One mesh for every splat: an irregular ten-sided disc lifted 1.2 mm off the surface it landed on.
        void RebuildSplats()
        {
            splatsDirty = false;
            if (!splatMesh) return;
            const int rim = 10;
            var vertices = new Vector3[splatCount * (rim + 1)]; var normals = new Vector3[vertices.Length];
            var triangles = new int[splatCount * rim * 3];
            for (int s = 0; s < splatCount; s++)
            {
                var splat = splats[s];
                Vector3 n = splat.normal, u = Vector3.Cross(n, Mathf.Abs(n.y) < .9f ? Vector3.up : Vector3.right).normalized, v = Vector3.Cross(n, u);
                Vector3 center = splat.point + n * .0012f, localNormal = splatRenderer.transform.InverseTransformDirection(n).normalized;
                int baseIndex = s * (rim + 1);
                vertices[baseIndex] = splatRenderer.transform.InverseTransformPoint(center); normals[baseIndex] = localNormal;
                for (int k = 0; k < rim; k++)
                {
                    float angle = k * Mathf.PI * 2 / rim, jag = .7f + .3f * Mathf.Abs(Mathf.Sin(splat.seed * 1.37f + k * 2.3f));
                    Vector3 world = center + (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * splat.radius * jag;
                    vertices[baseIndex + 1 + k] = splatRenderer.transform.InverseTransformPoint(world); normals[baseIndex + 1 + k] = localNormal;
                    int t = (s * rim + k) * 3;
                    triangles[t] = baseIndex; triangles[t + 1] = baseIndex + 1 + k; triangles[t + 2] = baseIndex + 1 + (k + 1) % rim;
                }
            }
            splatMesh.Clear(); splatMesh.vertices = vertices; splatMesh.normals = normals; splatMesh.triangles = triangles; splatMesh.RecalculateBounds();
            splatRenderer.enabled = Visible && splatCount > 0;
        }

        void Build()
        {
            if (drops) return;
            material = TissueRuntimeMaterial.Create("SimulatedBlood", Blood);
            material.SetFloat("_Glossiness", .72f); material.SetFloat("_Metallic", 0);
            dropMesh = DropMesh();
            if (!TryGetComponent(out drops)) drops = gameObject.AddComponent<ParticleSystem>();
            drops.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = drops.main;
            main.playOnAwake = false; main.loop = true; main.maxParticles = MaxParticles; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Local; main.gravityModifier = 1; main.startLifetime = 1.6f; main.startSize = .0035f; main.startColor = Blood;
            var emission = drops.emission; emission.enabled = false; // Script-emitted from sim sources only.
            var shape = drops.shape; shape.enabled = false;
            // Low bounce, high dampening: a drop dies on its first static surface (patient, table, floor) and splats there.
            var collision = drops.collision;
            collision.enabled = true; collision.type = ParticleSystemCollisionType.World; collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.bounce = .05f; collision.dampen = .9f; collision.lifetimeLoss = 1; collision.radiusScale = .5f;
            collision.quality = ParticleSystemCollisionQuality.Medium; collision.maxCollisionShapes = 32; collision.enableDynamicColliders = false;
            collision.collidesWith = ~0; collision.sendCollisionMessages = true;
            var renderer = GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh; renderer.mesh = dropMesh; renderer.sharedMaterial = material;
            renderer.alignment = ParticleSystemRenderSpace.World;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            drops.Play();
            var splatObject = new GameObject("BloodSplats"); splatObject.transform.SetParent(transform, false);
            splatMesh = new Mesh { name = "BloodSplats" }; splatMesh.MarkDynamic();
            splatObject.AddComponent<MeshFilter>().sharedMesh = splatMesh;
            splatRenderer = splatObject.AddComponent<MeshRenderer>(); splatRenderer.sharedMaterial = material;
            splatRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; splatRenderer.enabled = false;
        }
        // An eight-faced unit drop: a few vertices per particle keeps hundreds of drops cheap on Quest.
        static Mesh DropMesh()
        {
            var points = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int i = 0; i < points.Length; i++) points[i] *= .5f;
            var mesh = new Mesh { name = "BloodDrop", vertices = points, triangles = new[] { 0,2,4, 2,1,4, 1,3,4, 3,0,4, 2,0,5, 1,2,5, 3,1,5, 0,3,5 } };
            mesh.normals = Array.ConvertAll(points, p => p.normalized); mesh.RecalculateBounds();
            return mesh;
        }
        static void Release(UnityEngine.Object value) { if (!value) return; if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
        void OnDisable() { if (drops) drops.Clear(); if (splatRenderer) splatRenderer.enabled = false; }
        void OnDestroy()
        {
            if (input) input.RegionInjured -= Injured;
            Release(material); Release(dropMesh); Release(splatMesh);
        }
    }
}
