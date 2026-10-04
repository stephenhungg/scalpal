using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Local mechanics only. No exercise events. Contacts use current deformed display triangles,
    // 128 material-surface probes/body and 1 mm padding, not a claim of complete continuous collision.
    public sealed class TissueContactSolver : IDisposable
    {
        public const int MaximumBodies = 3, MaximumProbes = 128;
        public const float ContactPaddingMeters = .001f, MaximumCorrectionMeters = .002f;
        public const int MaximumTerminalLoops=12;
        public const float MaximumCapPlaneErrorMeters=.00005f;
        public int SupportedBodies { get; private set; }
        public int ActiveBodies { get; private set; }
        public int AppliedPairs { get; private set; }
        public int RejectedPairs { get; private set; }
        public int TriangleQueries { get; private set; }
        public int AppliedConstraints { get; private set; }
        readonly Contact[] contactBuffer = new Contact[MaximumProbes*2];
        // Opt-in attachment mode reports excess beyond each authored probe allowance; default reports geometric overlap.
        public float MaximumResidualPenetrationMeters { get; private set; }
        public float MaximumExcessResidualMeters => MaximumResidualPenetrationMeters;
        public float MaximumAuthoredOverlapMeters { get; private set; }
        public bool PreservesAuthoredRestOverlap { get; private set; }
        // Conservative surface-to-probe coverage bound, including allowed cage displacement.
        public float MaximumSamplingGapMeters { get; private set; }
        public string Status { get; private set; } = "Contact uninitialized";
        public struct SurfaceReport
        {
            public string BodyId, FailureReason, CapFailureReason;
            public int Vertices, Triangles, ProbeCount, OpenOrNonmanifoldEdges, DegenerateTriangles;
            public int OriginalOpenOrNonmanifoldEdges, CapLoops, AdditionalCapTriangles, ContactTriangles;
            public int BoundaryEdgeCount, NonmanifoldEdgeCount, InconsistentWindingEdgeCount;
            public int OriginalBoundaryEdgeCount, OriginalNonmanifoldEdgeCount, OriginalInconsistentWindingEdgeCount;
            public float MaximumCapPlaneErrorMeters;
            public bool ArtificiallyCappedContactProxy;
            public float SignedReferenceVolumeM3, SamplingGapSourceMeters, SourceUnitScale;
            public Vector3 RawBoundsSize;
            public bool Supported, FiniteGeometry;
        }
        public IReadOnlyList<SurfaceReport> SurfaceReports => reports;
        readonly List<SurfaceReport> reports = new List<SurfaceReport>();
        readonly List<Body> bodies = new List<Body>();
        sealed class Body
        {
            public DeformableTissue tissue;
            public string id;
            public Vector3[] source, world;
            public readonly Vector3[] queryPoints = new Vector3[MaximumProbes];
            public TissueSurfaceBvh acceleration;
            public int[] triangles;
            public Probe[] probes;
            public float sourceGap, scale, sourcePadding, authoredOverlap;
            public readonly Dictionary<Body,float[]> authoredAllowances=new Dictionary<Body,float[]>();
            public Bounds bounds;
            public bool ready, requireBaseline, baselineValid;
        }
        struct Probe { public int vertex, triangle; public Vector3 rest; }
        struct Contact
        {
            public Body a,b;
            public Vector3 restA,restB,normal;
            public float depth, desiredSeparation;
        }

        public void Initialize(IReadOnlyList<DeformableTissue> tissues,bool preserveRestOverlap=false)
        {
            Dispose(); reports.Clear(); SupportedBodies=0; PreservesAuthoredRestOverlap=preserveRestOverlap; MaximumAuthoredOverlapMeters=0; ResetStatistics();
            if(tissues==null || tissues.Count>MaximumBodies) {Status="Contact body count exceeds bounded solver";return;}
            var seen=new HashSet<DeformableTissue>();
            foreach(var tissue in tissues)
            {
                if(!tissue || !seen.Add(tissue) || tissue.Cage==null || !tissue.SourceMesh || !tissue.SourceMesh.isReadable) continue;
                var source=tissue.SourceMesh.vertices;var triangles=tissue.SourceMesh.triangles;
                var metricSource=new Vector3[source.Length];for(int i=0;i<source.Length;i++)metricSource[i]=tissue.ToMeters(source[i]);
                var part=tissue.GetComponent<AnatomyPart>();var report=AnalyzeSurface(metricSource,triangles);
                report.OriginalOpenOrNonmanifoldEdges=report.OpenOrNonmanifoldEdges;
                report.OriginalBoundaryEdgeCount=report.BoundaryEdgeCount;report.OriginalNonmanifoldEdgeCount=report.NonmanifoldEdgeCount;report.OriginalInconsistentWindingEdgeCount=report.InconsistentWindingEdgeCount;
                report.ContactTriangles=report.Triangles;
                report.SourceUnitScale=tissue.SourceUnitScale;report.RawBoundsSize=tissue.SourceMesh.bounds.size;
                report.BodyId=part?part.stableId:tissue.name;
                if(!report.Supported&&part&&part.stableId=="appendicular_artery"&&report.FiniteGeometry&&report.DegenerateTriangles==0)
                {
                    if(TryCapTerminalLoops(metricSource,triangles,out var cappedMeters,out var cappedTriangles,out int loops,out float error,out string capFailure))
                    {
                        var closed=AnalyzeSurface(cappedMeters,cappedTriangles);
                        if(closed.Supported)
                        {
                            report.Supported=true;report.FailureReason="";report.CapFailureReason="";report.ArtificiallyCappedContactProxy=true;report.CapLoops=loops;
                            report.BoundaryEdgeCount=closed.BoundaryEdgeCount;report.NonmanifoldEdgeCount=closed.NonmanifoldEdgeCount;report.InconsistentWindingEdgeCount=closed.InconsistentWindingEdgeCount;
                            report.MaximumCapPlaneErrorMeters=error;report.AdditionalCapTriangles=(cappedTriangles.Length-triangles.Length)/3;
                            report.OpenOrNonmanifoldEdges=closed.OpenOrNonmanifoldEdges;report.SignedReferenceVolumeM3=closed.SignedReferenceVolumeM3;report.ContactTriangles=closed.Triangles;
                            source=new Vector3[cappedMeters.Length];for(int i=0;i<source.Length;i++)source[i]=tissue.FromMeters(cappedMeters[i]);
                            triangles=cappedTriangles;
                        }
                        else report.CapFailureReason="Generated proxy: "+closed.FailureReason;
                    }
                    else report.CapFailureReason=capFailure;
                    report.CapLoops=loops;report.MaximumCapPlaneErrorMeters=error;
                }
                if(!report.Supported) {reports.Add(report);continue;}
                var body=new Body {id=report.BodyId,tissue=tissue,source=source,triangles=triangles,world=new Vector3[source.Length],requireBaseline=PreservesAuthoredRestOverlap};
                var probes=new List<Probe>();
                int originalVertexCount=report.Vertices,originalTriangleCount=report.Triangles;
                int vertexCount=Mathf.Min(originalVertexCount,MaximumProbes/2),triangleCount=Mathf.Min(originalTriangleCount,MaximumProbes-vertexCount);
                for(int i=0;i<vertexCount;i++) {int v=i*originalVertexCount/vertexCount;probes.Add(new Probe {vertex=v,triangle=-1,rest=source[v]});}
                for(int i=0;i<triangleCount;i++)
                {
                    int t=(i*originalTriangleCount/triangleCount)*3;
                    probes.Add(new Probe {vertex=-1,triangle=t,rest=(source[triangles[t]]+source[triangles[t+1]]+source[triangles[t+2]])/3});
                }
                body.probes=probes.ToArray();
                body.acceleration=new TissueSurfaceBvh(source,triangles,MaximumProbes);
                float vertexGap=0,edge=0;
                foreach(var vertex in source)
                {
                    float nearest=float.PositiveInfinity;foreach(var probe in probes)nearest=Mathf.Min(nearest,(vertex-probe.rest).magnitude);
                    vertexGap=Mathf.Max(vertexGap,nearest);
                }
                for(int i=0;i<triangles.Length;i+=3)
                {var a=source[triangles[i]];var b=source[triangles[i+1]];var c=source[triangles[i+2]];edge=Mathf.Max(edge,Vector3.Distance(a,b),Vector3.Distance(b,c),Vector3.Distance(c,a));}
                body.sourceGap=(vertexGap+edge)*tissue.SourceUnitScale;bodies.Add(body);
                report.ProbeCount=body.probes.Length;report.SamplingGapSourceMeters=body.sourceGap;reports.Add(report);
            }
            SupportedBodies=bodies.Count;
            if(PreservesAuthoredRestOverlap)CaptureAuthoredAllowances();
            Status=SupportedBodies==tissues.Count ? "Sampled surface contact ready" : "Some contact bodies skipped: missing, open or inward surface";
        }
        // Diagnostic copies expose generated contact geometry only; imported/rendered geometry stays unchanged.
        public bool TryCopyContactProxy(string bodyId,out Vector3[] rawVertices,out int[] triangleIndices)
        {
            foreach(var body in bodies)if(body.id==bodyId){rawVertices=(Vector3[])body.source.Clone();triangleIndices=(int[])body.triangles.Clone();return true;}
            rawVertices=null;triangleIndices=null;return false;
        }

        // Read-only contact audit at the current shape; useful before enabling constraints on attached source structures.
        public float Measure(bool registrationValid)
        {
            ResetStatistics();
            if(!registrationValid){Status="Contact paused: registration invalid";return 0;}
            foreach(var body in bodies)
            {
                body.ready=Refresh(body);
                if(body.ready&&PreservesAuthoredRestOverlap)MaximumAuthoredOverlapMeters=Mathf.Max(MaximumAuthoredOverlapMeters,body.authoredOverlap*body.scale);
                if(body.ready){ActiveBodies++;MaximumSamplingGapMeters=Mathf.Max(MaximumSamplingGapMeters,(body.sourceGap+2*body.tissue.Cage.Preset.maxDisplacement)*body.scale);}
            }
            for(int a=0;a<bodies.Count;a++)for(int b=a+1;b<bodies.Count;b++)
                if(bodies[a].ready&&bodies[b].ready&&bodies[a].bounds.Intersects(bodies[b].bounds))
                    MaximumResidualPenetrationMeters=Mathf.Max(MaximumResidualPenetrationMeters,Residual(Deepest(bodies[a],bodies[b])));
            Status="Sampled contact measured without corrections";return MaximumResidualPenetrationMeters;
        }
        public void Solve(bool registrationValid)
        {
            ResetStatistics();
            if(!registrationValid) {Status="Contact paused: registration invalid";return;}
            foreach(var body in bodies)
            {
                body.ready=Refresh(body);
                if(body.ready&&PreservesAuthoredRestOverlap)MaximumAuthoredOverlapMeters=Mathf.Max(MaximumAuthoredOverlapMeters,body.authoredOverlap*body.scale);
                if(body.ready)ActiveBodies++;
                if(body.ready)MaximumSamplingGapMeters=Mathf.Max(MaximumSamplingGapMeters,(body.sourceGap+2*body.tissue.Cage.Preset.maxDisplacement)*body.scale);
            }
            int appliedMask=0,rejectedMask=0;
            // Project every penetrating sample, after each 90 Hz elastic substep. Two
            // bounded passes revisit the deformed surfaces; pins and displacement caps
            // still fail closed. This is sampled positional contact, not complete CCD.
            for(int iteration=0;iteration<2;iteration++)
            {
                int before=AppliedConstraints,pair=0;
                MaximumResidualPenetrationMeters=0;
                for(int a=0;a<bodies.Count;a++)for(int b=a+1;b<bodies.Count;b++,pair++)
                {
                    var first=bodies[a];var second=bodies[b];if(!first.ready||!second.ready||!first.bounds.Intersects(second.bounds))continue;
                    int count=0;Gather(first,second,ref count);Gather(second,first,ref count);
                    for(int i=0;i<count;i++)
                    {
                        var contact=contactBuffer[i];MaximumResidualPenetrationMeters=Mathf.Max(MaximumResidualPenetrationMeters,Residual(contact));
                        if(TryProjectContact(contact,out bool rejected))
                        {appliedMask|=1<<pair;AppliedConstraints++;}
                        else if(rejected)rejectedMask|=1<<pair;
                    }
                    if(count>0){Refresh(first);Refresh(second);}
                }
                if(AppliedConstraints==before)break;
            }
            for(int pair=0;pair<3;pair++){if((appliedMask&(1<<pair))!=0)AppliedPairs++;if((rejectedMask&(1<<pair))!=0)RejectedPairs++;}
            if(AppliedConstraints>0)
            {
                MaximumResidualPenetrationMeters=0;
                for(int a=0;a<bodies.Count;a++)for(int b=a+1;b<bodies.Count;b++)
                    if(bodies[a].ready&&bodies[b].ready&&bodies[a].bounds.Intersects(bodies[b].bounds))
                        MaximumResidualPenetrationMeters=Mathf.Max(MaximumResidualPenetrationMeters,Residual(Deepest(bodies[a],bodies[b])));
            }
            Status=ActiveBodies!=bodies.Count?"Some contact bodies inactive, hidden or in an unsupported transform":
                RejectedPairs>0?"Sampled contact unresolved: pinned, capped or unsafe correction":"Sampled contact evaluated; unsampled intersections remain possible";
        }
        bool TryProjectContact(Contact contact,out bool rejected)
        {
            rejected=false;
            // Every cage projection moves multiple later probes in this gathered
            // batch. Re-evaluate their frozen material plane before applying them;
            // stale initial depths over-project dense surfaces. Deform is linear in
            // the current cage nodes. The next bounded pass refits geometry/normals.
            float remaining=contact.desiredSeparation-Vector3.Dot(MaterialPoint(contact.a,contact.restA)-MaterialPoint(contact.b,contact.restB),contact.normal);
            if(remaining<=1e-7f)return false;
            float distance=Mathf.Min(remaining*.5f,MaximumCorrectionMeters,Mathf.Min(contact.a.scale,contact.b.scale)*.004f);
            Vector3 move=contact.normal*distance;
            if(contact.a.tissue.Cage.TryContactCandidate(contact.a.tissue.ToMeters(contact.restA),contact.a.tissue.ToMeters(contact.a.tissue.transform.InverseTransformVector(move*.5f)),out var candidateA)
                &&contact.b.tissue.Cage.TryContactCandidate(contact.b.tissue.ToMeters(contact.restB),contact.b.tissue.ToMeters(contact.b.tissue.transform.InverseTransformVector(-move*.5f)),out var candidateB))
            {
                contact.a.tissue.CommitContact(candidateA);contact.b.tissue.CommitContact(candidateB);return true;
            }
            rejected=true;return false;
        }
        public void Dispose() { foreach(var body in bodies)body.acceleration?.Dispose(); bodies.Clear(); SupportedBodies=0; }
        void ResetStatistics(){ActiveBodies=AppliedPairs=RejectedPairs=TriangleQueries=AppliedConstraints=0;MaximumResidualPenetrationMeters=MaximumSamplingGapMeters=0;MaximumAuthoredOverlapMeters=0;}
        float Residual(Contact contact)=>Mathf.Max(0,contact.depth-(PreservesAuthoredRestOverlap?0:ContactPaddingMeters));
        // These fixed per-probe source-frame allowances preserve authored embedded attachments.
        // They are not inferred anatomical constraints and do not exempt an entire pair from contact.
        void CaptureAuthoredAllowances()
        {
            foreach(var body in bodies)
            {
                body.ready=Refresh(body,true);body.baselineValid=body.ready;
                if(body.ready)body.sourcePadding=ContactPaddingMeters/body.scale;
            }
            foreach(var from in bodies)foreach(var target in bodies)
            {
                if(from==target||!from.ready||!target.ready)continue;
                var allowances=new float[from.probes.Length];
                QueryBatch(from,target);
                for(int i=0;i<from.probes.Length;i++)
                {
                    if(!target.bounds.Contains(WorldProbe(from,from.probes[i]))||!QuerySurface(target,i,out float penetration,out _,out _))continue;
                    from.authoredOverlap=Mathf.Max(from.authoredOverlap,Mathf.Max(0,penetration)/from.scale);
                    allowances[i]=Mathf.Max(0,penetration+ContactPaddingMeters)/from.scale;
                }
                from.authoredAllowances.Add(target,allowances);
            }
            foreach(var body in bodies)if(body.ready)MaximumAuthoredOverlapMeters=Mathf.Max(MaximumAuthoredOverlapMeters,body.authoredOverlap*body.scale);
        }
        static bool Refresh(Body body,bool authoredRest=false)
        {
            var tissue=body.tissue;
            if(!authoredRest&&body.requireBaseline&&!body.baselineValid)return false;
            if(!tissue||(!authoredRest&&!tissue.isActiveAndEnabled)||tissue.Cage==null||tissue.SourceMesh==null) return false;
            var collider=tissue.GetComponent<MeshCollider>();var part=tissue.GetComponent<AnatomyPart>();
            if(!collider||(!authoredRest&&(!collider.enabled||!collider.gameObject.activeInHierarchy||(part&&(!part.IsVisible||!part.HasVisibleGeometry)))))return false;
            var scale=tissue.transform.lossyScale;
            if(!TissueCage.Finite(scale)||scale.x<=0||scale.y<=0||scale.z<=0||Mathf.Abs(scale.x-scale.y)>scale.x*.001f||Mathf.Abs(scale.x-scale.z)>scale.x*.001f) return false;
            body.scale=scale.x/tissue.SourceUnitScale;
            for(int i=0;i<body.source.Length;i++)
            {
                body.world[i]=tissue.transform.TransformPoint(authoredRest?body.source[i]:tissue.DeformSurfacePoint(body.source[i]));
                if(!TissueCage.Finite(body.world[i])) return false;
                if(i==0)body.bounds=new Bounds(body.world[i],Vector3.zero);else body.bounds.Encapsulate(body.world[i]);
            }
            body.bounds.Expand(Mathf.Max(ContactPaddingMeters,body.sourcePadding*body.scale)*2);
            body.acceleration.Refit(body.world);return true;
        }
        static Vector3 WorldProbe(Body body,Probe probe) => probe.vertex>=0?body.world[probe.vertex]:
            (body.world[body.triangles[probe.triangle]]+body.world[body.triangles[probe.triangle+1]]+body.world[body.triangles[probe.triangle+2]])/3;
        Contact Deepest(Body a,Body b)
        {
            var result=default(Contact);ProbeAgainst(a,b,ref result);ProbeAgainst(b,a,ref result);return result;
        }
        void Gather(Body from,Body target,ref int count)
        {
            from.authoredAllowances.TryGetValue(target,out var allowances);QueryBatch(from,target);
            for(int i=0;i<from.probes.Length;i++)if(ReadContact(from,target,i,allowances,out var contact))contactBuffer[count++]=contact;
        }
        void ProbeAgainst(Body from,Body target,ref Contact deepest)
        {
            from.authoredAllowances.TryGetValue(target,out var allowances);QueryBatch(from,target);
            for(int i=0;i<from.probes.Length;i++)
                if(ReadContact(from,target,i,allowances,out var contact)&&contact.depth>deepest.depth)deepest=contact;
        }
        bool ReadContact(Body from,Body target,int i,float[] allowances,out Contact contact)
        {
            contact=default;var probe=from.probes[i];
            if(!target.bounds.Contains(WorldProbe(from,probe))||!QuerySurface(target,i,out float penetration,out var normal,out var rest))return false;
            float padding=PreservesAuthoredRestOverlap?from.sourcePadding*from.scale:ContactPaddingMeters;
            float depth=penetration+padding;
            if(PreservesAuthoredRestOverlap&&allowances!=null)depth-=allowances[i]*from.scale;
            if(depth<=(PreservesAuthoredRestOverlap?1e-6f:1e-7f))return false;
            float separation=Vector3.Dot(MaterialPoint(from,probe.rest)-MaterialPoint(target,rest),normal);
            contact=new Contact {a=from,b=target,restA=probe.rest,restB=rest,normal=normal,depth=depth,desiredSeparation=separation+depth};return true;
        }
        static Vector3 MaterialPoint(Body body,Vector3 rest) => body.tissue.transform.TransformPoint(body.tissue.DeformSurfacePoint(rest));
        void QueryBatch(Body from,Body target)
        {
            for(int i=0;i<from.probes.Length;i++)from.queryPoints[i]=WorldProbe(from,from.probes[i]);
            target.acceleration.Query(from.queryPoints,from.probes.Length);
            TriangleQueries+=target.acceleration.LastTriangleTests;
        }
        static bool QuerySurface(Body target,int query,out float penetration,out Vector3 normal,out Vector3 rest)
        {
            var hit=target.acceleration.Result(query);penetration=hit.penetration;normal=hit.normal;rest=Vector3.zero;
            if(hit.triangle<0)return false;
            int t=hit.triangle*3;
            rest=target.source[target.triangles[t]]*hit.barycentric.x+target.source[target.triangles[t+1]]*hit.barycentric.y+target.source[target.triangles[t+2]]*hit.barycentric.z;
            return true;
        }
        struct DirectedEdge { public int from,to,count; }
        // Only coherent simple planar vessel-end loops get a disclosed artificial contact cap.
        // No source triangles are repaired, removed or persisted; nonmanifold/branching holes fail closed.
        static bool TryCapTerminalLoops(Vector3[] meters,int[] triangles,out Vector3[] capped,out int[] indices,out int loopCount,out float maximumError,out string failure)
        {
            capped=null;indices=null;loopCount=0;maximumError=0;failure="";
            var representative=new Dictionary<string,int>();var canonical=new int[meters.Length];
            for(int i=0;i<meters.Length;i++){string key=Key(meters[i]);if(!representative.TryGetValue(key,out int index)){index=i;representative.Add(key,index);}canonical[i]=index;}
            var edges=new Dictionary<string,DirectedEdge>();
            for(int i=0;i<triangles.Length;i+=3)for(int edge=0;edge<3;edge++)
            {
                int a=canonical[triangles[i+edge]],b=canonical[triangles[i+(edge+1)%3]];if(a==b){failure="Collapsed welded edge";return false;}
                string key=Mathf.Min(a,b)+":"+Mathf.Max(a,b);
                if(edges.TryGetValue(key,out var stored))
                {
                    if(stored.count!=1||stored.from!=b||stored.to!=a){failure="Nonmanifold edge or inconsistent directed winding";return false;}
                    stored.count=2;edges[key]=stored;
                }
                else edges.Add(key,new DirectedEdge {from=a,to=b,count=1});
            }
            var next=new Dictionary<int,int>();var incoming=new Dictionary<int,int>();
            foreach(var edge in edges.Values)if(edge.count==1)
            {
                if(next.ContainsKey(edge.from)||incoming.ContainsKey(edge.to)){failure="Branching boundary";return false;}
                next.Add(edge.from,edge.to);incoming.Add(edge.to,edge.from);
            }
            if(next.Count==0){failure="No terminal boundary";return false;}
            foreach(int vertex in next.Keys)if(!incoming.ContainsKey(vertex)){failure="Incomplete boundary degree";return false;}
            var visited=new HashSet<int>();var outputVertices=new List<Vector3>(meters);var outputTriangles=new List<int>(triangles);
            foreach(int start in next.Keys)
            {
                if(visited.Contains(start))continue;
                var loop=new List<int>();int cursor=start;
                do
                {
                    if(visited.Contains(cursor)||!next.TryGetValue(cursor,out int following)){failure="Boundary is not a simple closed loop";return false;}
                    visited.Add(cursor);loop.Add(cursor);cursor=following;
                    if(loop.Count>next.Count){failure="Boundary traversal budget";return false;}
                }while(cursor!=start);
                if(loop.Count<3){failure="Terminal loop has fewer than three vertices";return false;}
                if(++loopCount>MaximumTerminalLoops){failure="Terminal loop budget exceeded";return false;}
                Vector3 center=Vector3.zero;foreach(int vertex in loop)center+=meters[vertex]/loop.Count;
                Vector3 area=Vector3.zero;for(int i=0;i<loop.Count;i++)area+=Vector3.Cross(meters[loop[i]]-center,meters[loop[(i+1)%loop.Count]]-center);
                if(!TryAreaNormal(area,1e-24f,out var normal)){failure="Degenerate terminal area";return false;}
                for(int i=0;i<loop.Count;i++)
                {
                    maximumError=Mathf.Max(maximumError,Mathf.Abs(Vector3.Dot(meters[loop[i]]-center,normal)));
                    if(maximumError>MaximumCapPlaneErrorMeters){failure="Terminal loop exceeds metric plane tolerance";return false;}
                    // A center fan must lie consistently inside the loop, never bridge a concave void.
                    if(Vector3.Dot(Vector3.Cross(meters[loop[i]]-center,meters[loop[(i+1)%loop.Count]]-center),normal)<=1e-14f){failure="Terminal center fan is degenerate or outside boundary";return false;}
                }
                int axis=Mathf.Abs(normal.x)>Mathf.Abs(normal.y)?0:1;if(Mathf.Abs(normal.z)>Mathf.Abs(normal[axis]))axis=2;
                Vector2 Project(Vector3 point)=>axis==0?new Vector2(point.y,point.z):axis==1?new Vector2(point.x,point.z):new Vector2(point.x,point.y);
                for(int i=0;i<loop.Count;i++)for(int j=i+1;j<loop.Count;j++)
                {
                    if(j==i+1||(i==0&&j==loop.Count-1))continue;
                    if(SegmentsCross(Project(meters[loop[i]]),Project(meters[loop[(i+1)%loop.Count]]),Project(meters[loop[j]]),Project(meters[loop[(j+1)%loop.Count]]))){failure="Self-intersecting terminal boundary";return false;}
                }
                int cap=outputVertices.Count;outputVertices.Add(center);
                for(int i=0;i<loop.Count;i++){outputTriangles.Add(loop[(i+1)%loop.Count]);outputTriangles.Add(loop[i]);outputTriangles.Add(cap);}
                if(outputTriangles.Count>9000){failure="Contact triangle budget exceeded";return false;}
            }
            if(visited.Count!=next.Count){failure="Unresolved terminal boundary";return false;}
            capped=outputVertices.ToArray();indices=outputTriangles.ToArray();return true;
        }
        static bool SegmentsCross(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
        {
            float Cross(Vector2 u,Vector2 v)=>u.x*v.y-u.y*v.x;
            var ab=b-a;var cd=d-c;float first=Cross(ab,c-a),second=Cross(ab,d-a),third=Cross(cd,a-c),fourth=Cross(cd,b-c);
            if(first*second<0&&third*fourth<0)return true;
            bool On(Vector2 p,Vector2 x,Vector2 y)=>Mathf.Abs(Cross(y-x,p-x))<1e-12f&&p.x>=Mathf.Min(x.x,y.x)-1e-7f&&p.x<=Mathf.Max(x.x,y.x)+1e-7f&&p.y>=Mathf.Min(x.y,y.y)-1e-7f&&p.y<=Mathf.Max(x.y,y.y)+1e-7f;
            return On(a,c,d)||On(b,c,d)||On(c,a,b)||On(d,a,b);
        }

        // Area vectors have units m²; Unity's length-based .normalized epsilon erases valid
        // sub-millimeter vessel triangles. Guard squared area explicitly, then divide directly.
        static bool TryAreaNormal(Vector3 area,float minimumSquaredArea,out Vector3 normal)
        {
            normal=Vector3.zero;float squared=area.sqrMagnitude;
            if(!TissueCage.Finite(area)||float.IsNaN(squared)||float.IsInfinity(squared)||squared<minimumSquaredArea)return false;
            normal=area/Mathf.Sqrt(squared);return TissueCage.Finite(normal);
        }
        static SurfaceReport AnalyzeSurface(Vector3[] vertices,int[] triangles)
        {
            var report=new SurfaceReport {Vertices=vertices.Length,Triangles=triangles.Length/3,FiniteGeometry=true,FailureReason="",CapFailureReason=""};
            if(vertices.Length==0||triangles.Length==0||triangles.Length%3!=0||triangles.Length>9000){report.FailureReason="Empty, malformed or over-budget surface";return report;}
            var edges=new Dictionary<string,int>();var directions=new Dictionary<string,int>();double volume=0;
            var origin=vertices[0];
            for(int i=0;i<triangles.Length;i+=3)
            {
                if(triangles[i]<0||triangles[i]>=vertices.Length||triangles[i+1]<0||triangles[i+1]>=vertices.Length||triangles[i+2]<0||triangles[i+2]>=vertices.Length)
                {report.FiniteGeometry=false;report.FailureReason="Triangle index outside vertex array";return report;}
                var a=vertices[triangles[i]];var b=vertices[triangles[i+1]];var c=vertices[triangles[i+2]];
                if(!TissueCage.Finite(a)||!TissueCage.Finite(b)||!TissueCage.Finite(c)){report.FiniteGeometry=false;report.FailureReason="Nonfinite vertex";return report;}
                if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-20f)report.DegenerateTriangles++;
                volume+=Vector3.Dot(a-origin,Vector3.Cross(b-origin,c-origin))/6d;
                Edge(edges,directions,a,b);Edge(edges,directions,b,c);Edge(edges,directions,c,a);
            }
            foreach(var edge in edges)
            {
                if(edge.Value==1)report.BoundaryEdgeCount++;
                else if(edge.Value!=2)report.NonmanifoldEdgeCount++;
                if(edge.Value==2&&directions[edge.Key]!=0)report.InconsistentWindingEdgeCount++;
            }
            report.OpenOrNonmanifoldEdges=report.BoundaryEdgeCount+report.NonmanifoldEdgeCount;
            report.SignedReferenceVolumeM3=(float)volume;
            report.Supported=report.FiniteGeometry&&report.DegenerateTriangles==0&&report.OpenOrNonmanifoldEdges==0&&report.InconsistentWindingEdgeCount==0&&volume>1e-12;
            if(!report.Supported)report.FailureReason=report.NonmanifoldEdgeCount>0?"Nonmanifold surface":report.InconsistentWindingEdgeCount>0?"Inconsistent directed winding":report.DegenerateTriangles>0?"Degenerate triangles":report.BoundaryEdgeCount>0?"Open boundary": "Inward or negligible signed volume";
            return report;
        }
        static string Key(Vector3 p)=>Mathf.RoundToInt(p.x*1e6f)+":"+Mathf.RoundToInt(p.y*1e6f)+":"+Mathf.RoundToInt(p.z*1e6f);
        static void Edge(Dictionary<string,int> edges,Dictionary<string,int> directions,Vector3 a,Vector3 b)
        {string x=Key(a),y=Key(b);bool ascending=string.CompareOrdinal(x,y)<0;string key=ascending?x+"/"+y:y+"/"+x;edges.TryGetValue(key,out int count);edges[key]=count+1;directions.TryGetValue(key,out int direction);directions[key]=direction+(ascending?1:-1);}
    }
}
