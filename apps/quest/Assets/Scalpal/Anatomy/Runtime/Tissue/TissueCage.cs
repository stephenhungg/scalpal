using System;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Original implementation of XPBD distance/volume constraints (Macklin et al., 2016).
    // An eight-node teaching cage, NOT a measured constitutive model of human tissue.
    public sealed class TissueCage
    {
        public readonly Vector3[] Rest = new Vector3[8];
        public readonly Vector3[] Positions = new Vector3[8];
        readonly Vector3[] previous = new Vector3[8], velocity = new Vector3[8];
        readonly float[] weights = new float[8];
        readonly int[] edgeA = new int[28], edgeB = new int[28];
        readonly float[] lengths = new float[28], edgeLambda = new float[28];
        // Five tetrahedra partition a box. Signed orientation is retained.
        static readonly int[,] Tet = { {0,1,2,4}, {1,2,3,7}, {1,4,5,7}, {2,4,6,7}, {1,2,4,7} };
        readonly float[] volumes = new float[5];
        readonly Bounds bounds;
        public readonly TissuePreset Preset;
        public int Handle { get; private set; } = -1;
        public float MaxDisplacement { get; private set; }

        public TissueCage(Bounds sourceBounds, TissuePreset preset)
        {
            if (!Finite(sourceBounds.center) || !Finite(sourceBounds.size) || sourceBounds.size.x <= 0 || sourceBounds.size.y <= 0 || sourceBounds.size.z <= 0)
                throw new ArgumentException("A tissue cage requires finite three-dimensional geometry.");
            bounds = sourceBounds; Preset = preset;
            for (int i = 0; i < 8; i++)
            {
                Rest[i] = new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
                // Posterior face attachment is an authored constraint, not detected mesentery.
                weights[i] = (i & 4) == 0 ? 1 : 0;
            }
            int edge = 0;
            for (int a = 0; a < 8; a++) for (int b = a + 1; b < 8; b++)
            { edgeA[edge] = a; edgeB[edge] = b; lengths[edge++] = Vector3.Distance(Rest[a], Rest[b]); }
            for (int t = 0; t < 5; t++) volumes[t] = Volume(Rest, t);
            Reset();
        }

        public bool BeginHandle(Vector3 localPoint)
        {
            if (!Finite(localPoint)) return false;
            float nearest = float.PositiveInfinity; Handle = -1;
            for (int i = 0; i < 8; i++) if (weights[i] > 0)
            {
                float d = (Positions[i] - localPoint).sqrMagnitude;
                if (d < nearest) { nearest = d; Handle = i; }
            }
            return Handle >= 0;
        }
        public void ReleaseHandle() => Handle = -1;
        public Vector3 HandlePosition => Handle < 0 ? Vector3.zero : Positions[Handle];

        public void Step(float seconds, Vector3 localHandleTarget)
        {
            if (!Finite(localHandleTarget) || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0) { ReleaseHandle(); return; }
            // The caller uses a fixed 1/90 s step; cap direct callers as well.
            float dt = Mathf.Min(seconds, 1f / 30f);
            Array.Clear(edgeLambda, 0, edgeLambda.Length);
            for (int i = 0; i < 8; i++)
            {
                previous[i] = Positions[i];
                if (weights[i] == 0) { Positions[i] = Rest[i]; velocity[i] = Vector3.zero; continue; }
                velocity[i] *= Mathf.Exp(-Preset.dampingPerSecond * dt);
                Positions[i] += velocity[i] * dt;
            }
            Vector3 goal = Handle < 0 ? Vector3.zero : Rest[Handle] + Vector3.ClampMagnitude(localHandleTarget - Rest[Handle], Preset.maxDisplacement);
            for (int iteration = 0; iteration < 8; iteration++)
            {
                for (int e = 0; e < 28; e++) SolveEdge(e, dt);
                for (int t = 0; t < 5; t++) SolveVolume(t);
                for (int i = 0; i < 8; i++)
                {
                    if (weights[i] == 0) Positions[i] = Rest[i];
                    else if (i != Handle) Positions[i] = Vector3.Lerp(Positions[i], Rest[i], 1f - Mathf.Exp(-Preset.returnPerSecond * dt / 8));
                }
                if (Handle >= 0) Positions[Handle] = goal;
            }
            MaxDisplacement = 0;
            for (int i = 0; i < 8; i++)
            {
                Positions[i] = Rest[i] + Vector3.ClampMagnitude(Positions[i] - Rest[i], Preset.maxDisplacement);
                if (!Finite(Positions[i])) { Reset(); return; }
                velocity[i] = Vector3.ClampMagnitude((Positions[i] - previous[i]) / dt, .5f);
                MaxDisplacement = Mathf.Max(MaxDisplacement, Vector3.Distance(Positions[i], Rest[i]));
            }
            // If a bounded pull still inverts a cell, discard it rather than rendering inside-out tissue.
            for (int t = 0; t < 5; t++) if (Volume(Positions, t) * volumes[t] <= 0) { Reset(); return; }
        }

        void SolveEdge(int e, float dt)
        {
            int a = edgeA[e], b = edgeB[e];
            Vector3 delta = Positions[a] - Positions[b]; float length = delta.magnitude;
            if (length < 1e-8f) return;
            float alpha = Preset.stretchCompliance / (dt * dt), denominator = weights[a] + weights[b] + alpha;
            if (denominator <= 0) return;
            float change = (-(length - lengths[e]) - alpha * edgeLambda[e]) / denominator;
            edgeLambda[e] += change; Vector3 correction = delta * (change / length);
            Positions[a] += weights[a] * correction; Positions[b] -= weights[b] * correction;
        }
        void SolveVolume(int t)
        {
            int a = Tet[t,0], b = Tet[t,1], c = Tet[t,2], d = Tet[t,3];
            Vector3 ab = Positions[b] - Positions[a], ac = Positions[c] - Positions[a], ad = Positions[d] - Positions[a];
            Vector3 gb = Vector3.Cross(ac, ad) / 6, gc = Vector3.Cross(ad, ab) / 6, gd = Vector3.Cross(ab, ac) / 6;
            Vector3 ga = -gb - gc - gd;
            float denominator = weights[a]*ga.sqrMagnitude + weights[b]*gb.sqrMagnitude + weights[c]*gc.sqrMagnitude + weights[d]*gd.sqrMagnitude;
            if (denominator < 1e-16f) return;
            // Zero-compliance volume projection; approximate due to finite iterations/handle constraint.
            float change = -(Volume(Positions,t) - volumes[t]) / denominator;
            Positions[a] += weights[a]*change*ga; Positions[b] += weights[b]*change*gb;
            Positions[c] += weights[c]*change*gc; Positions[d] += weights[d]*change*gd;
        }
        static float Volume(Vector3[] p, int t) => Vector3.Dot(p[Tet[t,1]] - p[Tet[t,0]],
            Vector3.Cross(p[Tet[t,2]] - p[Tet[t,0]], p[Tet[t,3]] - p[Tet[t,0]])) / 6;
        public float VolumeRatio
        {
            get { float current = 0, rest = 0; for (int t=0;t<5;t++) { current += Mathf.Abs(Volume(Positions,t)); rest += Mathf.Abs(volumes[t]); } return current/rest; }
        }
        // Trilinear embedding preserves every source vertex at rest; the cage carries deformation.
        public Vector3 Deform(Vector3 point)
        {
            Vector3 u = new Vector3(Mathf.InverseLerp(bounds.min.x,bounds.max.x,point.x),
                Mathf.InverseLerp(bounds.min.y,bounds.max.y,point.y), Mathf.InverseLerp(bounds.min.z,bounds.max.z,point.z));
            Vector3 offset = Vector3.zero;
            for (int i=0;i<8;i++) offset += (Positions[i]-Rest[i]) * ((i&1)==0?1-u.x:u.x) * ((i&2)==0?1-u.y:u.y) * ((i&4)==0?1-u.z:u.z);
            return point + offset;
        }
        // Contact point/correction are in the source-local meter frame. The caller transforms world contact explicitly.
        public bool ApplyContact(Vector3 localPoint, Vector3 localCorrection)
        {
            if (!TryContactCandidate(localPoint, localCorrection, out var candidate)) return false;
            CommitContact(candidate); return true;
        }
        internal bool TryContactCandidate(Vector3 point, Vector3 correction, out Vector3[] candidate)
        {
            candidate = null;
            if (!Finite(point) || !Finite(correction) || correction.sqrMagnitude < 1e-14f || correction.magnitude > .005f) return false;
            Vector3 u = new Vector3(Mathf.InverseLerp(bounds.min.x,bounds.max.x,point.x),
                Mathf.InverseLerp(bounds.min.y,bounds.max.y,point.y),Mathf.InverseLerp(bounds.min.z,bounds.max.z,point.z));
            var influence = new float[8]; float denominator = 0;
            for (int i=0;i<8;i++)
            {
                influence[i] = ((i&1)==0?1-u.x:u.x)*((i&2)==0?1-u.y:u.y)*((i&4)==0?1-u.z:u.z);
                denominator += weights[i]*influence[i]*influence[i];
            }
            if (denominator < .00001f) return false;
            candidate = (Vector3[])Positions.Clone();
            for (int i=0;i<8;i++)
            {
                if (weights[i] == 0) continue;
                candidate[i] += correction*(weights[i]*influence[i]/denominator);
                candidate[i] = Rest[i]+Vector3.ClampMagnitude(candidate[i]-Rest[i],Preset.maxDisplacement);
                if (!Finite(candidate[i])) { candidate=null; return false; }
            }
            for (int t=0;t<5;t++) if (Volume(candidate,t)*volumes[t]<=0 || Mathf.Abs(Volume(candidate,t)/volumes[t]) < .02f)
            { candidate=null; return false; }
            Vector3 achieved=Vector3.zero;
            for (int i=0;i<8;i++) achieved += (candidate[i]-Positions[i])*influence[i];
            if (Vector3.Dot(achieved,correction) <= correction.sqrMagnitude*.01f) { candidate=null; return false; }
            return true;
        }
        internal void CommitContact(Vector3[] candidate)
        {
            Array.Copy(candidate,Positions,8); Array.Copy(candidate,previous,8); Array.Clear(velocity,0,8);
            MaxDisplacement=0;
            for(int i=0;i<8;i++) MaxDisplacement=Mathf.Max(MaxDisplacement,Vector3.Distance(Positions[i],Rest[i]));
        }

        public void Reset()
        {
            Array.Copy(Rest,Positions,8); Array.Copy(Rest,previous,8); Array.Clear(velocity,0,8); Handle=-1; MaxDisplacement=0;
        }
        public static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
    }

    [Serializable]
    public struct TissuePreset
    {
        // Artistic normalized compliance with unit inverse masses; not Pa or viscosity in Pa.s.
        public float stretchCompliance, dampingPerSecond, returnPerSecond, maxDisplacement;
        public static TissuePreset Bowel => new TissuePreset { stretchCompliance=.00006f, dampingPerSecond=18, returnPerSecond=7, maxDisplacement=.018f };
        public static TissuePreset Mesentery => new TissuePreset { stretchCompliance=.00015f, dampingPerSecond=24, returnPerSecond=5, maxDisplacement=.022f };
        public static TissuePreset Artery => new TissuePreset { stretchCompliance=.000012f, dampingPerSecond=30, returnPerSecond=12, maxDisplacement=.008f };
    }
}
