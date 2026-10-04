using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Conforming tetrahedral material mesh with discrete blade-induced face fracture.
    // Cuts follow the explicit cell resolution; this is not arbitrary-resolution remeshing.
    public sealed class TissueVolume
    {
        public struct Cell
        {
            public int a,b,c,d,material;
            public int Vertex(int i) => i==0?a:i==1?b:i==2?c:d;
        }
        public struct Face
        {
            public int a,b,c,cell,neighbor;
            public bool cut;
        }
        public readonly Vector3[] Original;
        public readonly Cell[] Cells;
        public readonly VolumeMaterial[] Materials;
        public readonly Face[] Faces;
        readonly bool[] originalPins;
        readonly Vector3[] boundaryTargets;
        readonly TissueTensor[] restInverse;
        readonly float[] restVolumes, lambdas;
        readonly MaxwellHistory[] histories;
        readonly int[,] binding;
        readonly List<int> cutCandidates=new List<int>();
        readonly List<Vector3> surfaceVertices=new List<Vector3>();
        readonly List<int>[] surfaceTriangles;
        Vector3[] positions, rest, previous, velocities, previousVelocities;
        float[] nodeMass;
        int[] handleNodes=Array.Empty<int>();
        Vector3[] handleOffsets=Array.Empty<Vector3>(), attachmentLambdas=Array.Empty<Vector3>();
        float[] handleWeights=Array.Empty<float>();
        Vector3 handleOrigin, acceptedHandleTarget;
        sealed class MaterialHandle
        {
            public int id, material, cell, a, b, c;
            public Vector3 barycentric, origin, acceptedTarget, requestedTarget, trialTarget;
            public int[] nodes = Array.Empty<int>();
            public Vector3[] offsets = Array.Empty<Vector3>(), multipliers = Array.Empty<Vector3>();
            public float[] weights = Array.Empty<float>();
        }
        readonly List<MaterialHandle> materialHandles = new List<MaterialHandle>();
        public const int MaximumMaterialHandles = 4;
        public int MaterialHandleCount => materialHandles.Count;
        public long AcceptedStepSequence { get; private set; }
        public int TopologyRevision { get; private set; }
        // Authored interaction tuning, not measured tissue material parameters.
        const float HandleRadiusMeters=.035f, HandleCompliance=.002f, HandleSpeedMetersPerSecond=.1f;
        public int HandleNodeCount => handleNodes.Length;
        public bool LastStepAccepted { get; private set; }
        public int LastStepRetries { get; private set; }
        public int LastStepBacktracks { get; private set; }
        public float LastRejectedJacobian { get; private set; }=float.PositiveInfinity;
        public int LastRejectedCell { get; private set; }=-1;
        float[] inverseMass;
        bool[] pinned;
        int[] originalNodeFor;
        Vector3[] acceptedForces, trialForces;
        public bool HasAcceptedStep { get; private set; }
        public Vector3[] Positions => positions;
        public Vector3[] Rest => rest;
        public float NodeMass(int node) => nodeMass[node];
        public int CutFaceCount { get; private set; }
        public int NodeCount => positions.Length;
        public float ReferenceVolume { get; private set; }
        public float TotalMass { get; private set; }
        public int Handle { get; private set; }=-1;
        public bool Dirty { get; private set; }=true;
        public const int MaxNodes=4096;
        public const int MaxCutFaces=512;
        const float NumericVelocityDamping=2; // Stability tuning, NOT measured tissue viscosity.

        public TissueVolume(Vector3[] nodes,Cell[] cells,VolumeMaterial[] materials,bool[] pins)
        {
            if (nodes==null || cells==null || materials==null || pins==null || nodes.Length!=pins.Length || cells.Length==0 || nodes.Length>MaxNodes)
                throw new ArgumentException("Invalid volume inputs");
            foreach(var material in materials) if(!material.HasValidUnits) throw new ArgumentException("Invalid SI material parameters");
            Original=(Vector3[])nodes.Clone(); Cells=(Cell[])cells.Clone(); Materials=(VolumeMaterial[])materials.Clone(); originalPins=(bool[])pins.Clone(); boundaryTargets=(Vector3[])nodes.Clone();
            restInverse=new TissueTensor[cells.Length]; restVolumes=new float[cells.Length]; lambdas=new float[cells.Length]; binding=new int[cells.Length,4];
            histories=new MaxwellHistory[cells.Length];
            for(int i=0;i<cells.Length;i++)
            {
                var cell=cells[i];
                for(int v=0;v<4;v++) if(cell.Vertex(v)<0 || cell.Vertex(v)>=nodes.Length || !TissueCage.Finite(nodes[cell.Vertex(v)])) throw new ArgumentException("Invalid cell vertex");
                if(cell.material<0 || cell.material>=materials.Length) throw new ArgumentException("Invalid cell material");
                var dm=new TissueTensor(nodes[cell.b]-nodes[cell.a],nodes[cell.c]-nodes[cell.a],nodes[cell.d]-nodes[cell.a]);
                float volume=Mathf.Abs(dm.Determinant)/6;
                if(float.IsNaN(volume)||float.IsInfinity(volume)||volume<1e-12f) throw new ArgumentException("Degenerate tissue cell");
                restVolumes[i]=volume; restInverse[i]=dm.Inverse(); ReferenceVolume+=volume;
                var material=materials[cell.material];
                histories[i]=new MaxwellHistory(material.relaxationFractions??Array.Empty<float>(),material.relaxationSeconds??Array.Empty<float>());
            }
            Faces=BuildFaces(cells);
            surfaceTriangles=new List<int>[Materials.Length];for(int i=0;i<surfaceTriangles.Length;i++)surfaceTriangles[i]=new List<int>();
            Reset();
        }
        static Face[] BuildFaces(Cell[] cells)
        {
            var faces=new List<Face>(); var owners=new Dictionary<string,int>();
            for(int i=0;i<cells.Length;i++) for(int opposite=0;opposite<4;opposite++)
            {
                var ids=new int[3]; int n=0;
                for(int v=0;v<4;v++) if(v!=opposite) ids[n++]=cells[i].Vertex(v);
                var sorted=(int[])ids.Clone(); Array.Sort(sorted); string key=sorted[0]+":"+sorted[1]+":"+sorted[2];
                if(owners.TryGetValue(key,out int index))
                {
                    var face=faces[index]; if(face.neighbor>=0) throw new ArgumentException("Nonmanifold material mesh");
                    face.neighbor=i; faces[index]=face;
                }
                else { owners.Add(key,faces.Count); faces.Add(new Face {a=ids[0],b=ids[1],c=ids[2],cell=i,neighbor=-1}); }
            }
            return faces.ToArray();
        }
        public int NodeFor(int cell,int corner) => binding[cell,corner];
        int BoundRoot(int cell,int root)
        {
            for(int i=0;i<4;i++) if(Cells[cell].Vertex(i)==root) return binding[cell,i];
            throw new InvalidOperationException("Face/root not owned by cell");
        }
        // Prescribed grips use original material-node identities, surviving cut fan duplication.
        // Only constructor-pinned nodes are eligible; pending targets apply on the next step.
        public bool SetBoundaryTarget(int originalNode, Vector3 meters)
        {
            if(originalNode<0||originalNode>=Original.Length||!originalPins[originalNode]||!TissueCage.Finite(meters))return false;
            boundaryTargets[originalNode]=meters; return true;
        }
        // Accepted material INTERNAL nodal forces, N. A grip reaction is their negative.
        // This excludes inertial, contact, damping and constraint-force contributions.
        // Free-node equilibrium residual must be checked before treating it as a coupon load.
        public void MeasureNodalForces(Vector3[] output)
        {
            if(output==null||output.Length!=NodeCount)throw new ArgumentException("Force buffer must match active material nodes");
            Array.Copy(acceptedForces,output,NodeCount);
        }
        public bool BeginHandle(Vector3 point,float maximumDistance)
        {
            ReleaseHandle();
            if(!TissueCage.Finite(point)||!(maximumDistance>0)||float.IsInfinity(maximumDistance))return false;
            float distance=maximumDistance*maximumDistance;
            for(int i=0;i<NodeCount;i++) if(!pinned[i])
            {
                float d=(positions[i]-point).sqrMagnitude;
                if(d<distance) {distance=d;Handle=i;}
            }
            if(Handle<0)return false;
            handleOrigin=acceptedHandleTarget=positions[Handle];
            Vector3 normal=Vector3.forward;float faceDistance=float.PositiveInfinity;
            foreach(var face in Faces)
            {
                if(face.neighbor>=0&&!face.cut)continue;
                float current=FaceDistanceSquared(face,face.cell,point);
                if(current>=faceDistance)continue;
                var candidate=Vector3.Cross(Original[face.b]-Original[face.a],Original[face.c]-Original[face.a]);
                if(candidate.sqrMagnitude<1e-15f)continue;
                faceDistance=current;normal=candidate.normalized;
            }
            // A finite material patch includes neighboring thin layers, avoiding a hard
            // skin-node indentation. Only the selected connected material fan is eligible:
            // attachment must never weld opposite sides of a complete cut back together.
            var reachable=new bool[NodeCount];reachable[Handle]=true;bool changed=true;
            while(changed)
            {
                changed=false;
                for(int cell=0;cell<Cells.Length;cell++)
                {
                    bool touches=false;for(int corner=0;corner<4;corner++)touches|=reachable[binding[cell,corner]];
                    if(!touches)continue;
                    for(int corner=0;corner<4;corner++)
                    {
                        int node=binding[cell,corner];
                        if(!reachable[node]&&(rest[node]-rest[Handle]).sqrMagnitude<=HandleRadiusMeters*HandleRadiusMeters)
                        {reachable[node]=true;changed=true;}
                    }
                }
            }
            var nodes=new List<int>();var offsets=new List<Vector3>();var weights=new List<float>();
            for(int node=0;node<NodeCount;node++)if(reachable[node]&&!pinned[node])
            {
                Vector3 offset=rest[node]-rest[Handle];
                // A layered patch translates thickness columns together: tapering with
                // depth would compress the 1 mm membrane under an inward grasp.
                float radius=(offset-normal*Vector3.Dot(offset,normal)).magnitude/HandleRadiusMeters;
                if(radius>=1&&node!=Handle)continue;
                nodes.Add(node);offsets.Add(positions[node]-handleOrigin);
                weights.Add(Mathf.Max(.05f,(1-radius)*(1-radius)));
            }
            handleNodes=nodes.ToArray();handleOffsets=offsets.ToArray();handleWeights=weights.ToArray();attachmentLambdas=new Vector3[handleNodes.Length];
            return true;
        }
        public void ReleaseHandle()
        {
            Handle=-1;handleNodes=Array.Empty<int>();handleOffsets=Array.Empty<Vector3>();
            handleWeights=Array.Empty<float>();attachmentLambdas=Array.Empty<Vector3>();
        }
        public Vector3 HandlePosition => Handle<0?Vector3.zero:positions[Handle];

        int MaterialIndex(string id)
        {
            for(int i=0;i<Materials.Length;i++)if(Materials[i].id==id)return i;
            return -1;
        }
        // Contacts are current exposed boundary/cut faces of the requested material.
        // Internal intact interfaces are not surface contacts.
        public bool TryMaterialContact(string id,Vector3 point,float radius,out Vector3 contact)
        {
            contact=Vector3.zero;
            return TryMaterialFace(id,point,radius,out _,out _,out contact);
        }
        bool TryMaterialFace(string id,Vector3 point,float radius,out int faceIndex,out int cell,out Vector3 contact)
        {
            faceIndex=cell=-1;contact=Vector3.zero;int material=MaterialIndex(id);
            if(material<0||!TissueCage.Finite(point)||!(radius>0)||float.IsInfinity(radius)||radius>.05f)return false;
            float nearest=radius*radius;
            for(int i=0;i<Faces.Length;i++)
            {
                var face=Faces[i];if(face.neighbor>=0&&!face.cut)continue;
                for(int side=0;side<(face.cut&&face.neighbor>=0?2:1);side++)
                {
                    int owner=side==0?face.cell:face.neighbor;if(Cells[owner].material!=material)continue;
                    Vector3 sample=ClosestTriangle(point,positions[BoundRoot(owner,face.a)],positions[BoundRoot(owner,face.b)],positions[BoundRoot(owner,face.c)]);
                    float distance=(point-sample).sqrMagnitude;if(distance>nearest)continue;
                    nearest=distance;faceIndex=i;cell=owner;contact=sample;
                }
            }
            return faceIndex>=0;
        }
        // IDs belong to the adapter (e.g. tool instance), never to a case step.
        // Each grip holds a connected, material-specific finite patch. Its actual contact
        // is a barycentric material face point, not the commanded controller position.
        public bool BeginMaterialHandle(int id,string materialId,Vector3 point,float radius)
        {
            foreach(var grip in materialHandles)if(grip.id==id)return false;
            if(materialHandles.Count>=MaximumMaterialHandles||!TryMaterialFace(materialId,point,radius,out int faceIndex,out int cell,out Vector3 contact))return false;
            var face=Faces[faceIndex];
            Vector3 a=positions[BoundRoot(cell,face.a)],b=positions[BoundRoot(cell,face.b)],c=positions[BoundRoot(cell,face.c)];
            Vector3 ab=b-a,ac=c-a,ap=contact-a;
            float aa=Vector3.Dot(ab,ab),bb=Vector3.Dot(ac,ac),dot=Vector3.Dot(ab,ac),denominator=aa*bb-dot*dot;
            if(Mathf.Abs(denominator)<1e-20f)return false;
            float u=(bb*Vector3.Dot(ap,ab)-dot*Vector3.Dot(ap,ac))/denominator;
            float v=(aa*Vector3.Dot(ap,ac)-dot*Vector3.Dot(ap,ab))/denominator;
            var handle=new MaterialHandle{id=id,material=MaterialIndex(materialId),cell=cell,a=face.a,b=face.b,c=face.c,
                barycentric=new Vector3(1-u-v,u,v),origin=contact,acceptedTarget=contact,requestedTarget=contact};
            if(!BuildMaterialPatch(handle))return false;
            materialHandles.Add(handle);return true;
        }
        Vector3 MaterialPosition(MaterialHandle grip)=>positions[BoundRoot(grip.cell,grip.a)]*grip.barycentric.x+
            positions[BoundRoot(grip.cell,grip.b)]*grip.barycentric.y+positions[BoundRoot(grip.cell,grip.c)]*grip.barycentric.z;
        bool BuildMaterialPatch(MaterialHandle grip)
        {
            int seed=-1;float nearest=float.PositiveInfinity;Vector3 anchor=MaterialPosition(grip);
            foreach(int root in new[]{grip.a,grip.b,grip.c})
            {
                int node=BoundRoot(grip.cell,root);float distance=(positions[node]-anchor).sqrMagnitude;
                if(!pinned[node]&&distance<nearest){seed=node;nearest=distance;}
            }
            if(seed<0)return false;
            Vector3 restAnchor=Original[grip.a]*grip.barycentric.x+Original[grip.b]*grip.barycentric.y+Original[grip.c]*grip.barycentric.z;
            Vector3 normal=Vector3.Cross(Original[grip.b]-Original[grip.a],Original[grip.c]-Original[grip.a]).normalized;
            var reachable=new bool[NodeCount];reachable[seed]=true;bool changed=true;
            while(changed)
            {
                changed=false;
                for(int cell=0;cell<Cells.Length;cell++)
                {
                    if(Cells[cell].material!=grip.material)continue;
                    bool touches=false;for(int corner=0;corner<4;corner++)touches|=reachable[binding[cell,corner]];
                    if(!touches)continue;
                    for(int corner=0;corner<4;corner++)
                    {
                        int node=binding[cell,corner];Vector3 offset=rest[node]-restAnchor;
                        if(reachable[node]||offset.sqrMagnitude>HandleRadiusMeters*HandleRadiusMeters)continue;
                        reachable[node]=true;changed=true;
                    }
                }
            }
            var nodes=new List<int>();var offsets=new List<Vector3>();var weights=new List<float>();
            for(int node=0;node<NodeCount;node++)if(reachable[node]&&!pinned[node])
            {
                Vector3 offset=rest[node]-restAnchor;
                float radius=(offset-normal*Vector3.Dot(offset,normal)).magnitude/HandleRadiusMeters;
                if(radius>=1&&node!=seed)continue;
                nodes.Add(node);offsets.Add(positions[node]-anchor);
                weights.Add(Mathf.Max(.05f,(1-radius)*(1-radius)));
            }
            grip.nodes=nodes.ToArray();grip.offsets=offsets.ToArray();grip.weights=weights.ToArray();grip.multipliers=new Vector3[grip.nodes.Length];
            return grip.nodes.Length>0;
        }
        public bool SetMaterialHandleTarget(int id,Vector3 meters)
        {
            if(!TissueCage.Finite(meters))return false;
            foreach(var grip in materialHandles)if(grip.id==id){grip.requestedTarget=meters;return true;}
            return false;
        }
        public bool TryMaterialHandlePosition(int id,out Vector3 meters)
        {
            foreach(var grip in materialHandles)if(grip.id==id){meters=MaterialPosition(grip);return true;}
            meters=Vector3.zero;return false;
        }
        public void ReleaseMaterialHandle(int id)
        {for(int i=materialHandles.Count-1;i>=0;i--)if(materialHandles[i].id==id)materialHandles.RemoveAt(i);}
        void ReleaseMaterialHandles()=>materialHandles.Clear();
        public int CutFacesForMaterial(string id)
        {
            int material=MaterialIndex(id),count=0;if(material<0)return 0;
            foreach(var face in Faces)if(face.cut&&(Cells[face.cell].material==material||face.neighbor>=0&&Cells[face.neighbor].material==material))count++;
            return count;
        }

        // A finite triangle swept by the real blade breaks intersected shared cell faces.
        public int CutSweep(Vector3 a,Vector3 b,Vector3 c,float contactTolerance)
            => FractureSweep(a,b,c,contactTolerance,-1);
        // Material-specific fracture does not cut intact inter-material interfaces.
        public int FractureMaterialSweep(string id,Vector3 a,Vector3 b,Vector3 c,float tolerance)
        {
            int material=MaterialIndex(id);return material<0?0:FractureSweep(a,b,c,tolerance,material);
        }
        int FractureSweep(Vector3 a,Vector3 b,Vector3 c,float contactTolerance,int material)
        {
            if(!TissueCage.Finite(a)||!TissueCage.Finite(b)||!TissueCage.Finite(c)||Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f) return 0;
            if(float.IsNaN(contactTolerance)||contactTolerance<0 || contactTolerance>.003f || CutFaceCount>=MaxCutFaces) return 0;
            var candidate=cutCandidates;candidate.Clear();
            for(int i=0;i<Faces.Length;i++)
            {
                var face=Faces[i]; if(face.neighbor<0 || face.cut) continue;
                if(material>=0&&(Cells[face.cell].material!=material||Cells[face.neighbor].material!=material))continue;
                Vector3 p=positions[BoundRoot(face.cell,face.a)],q=positions[BoundRoot(face.cell,face.b)],r=positions[BoundRoot(face.cell,face.c)];
                if(BladeIntersectsFace(a,b,c,p,q,r,contactTolerance)) candidate.Add(i);
            }
            if(candidate.Count==0 || CutFaceCount+candidate.Count>MaxCutFaces) return 0;
            foreach(int i in candidate) {var face=Faces[i];face.cut=true;Faces[i]=face;}
            // Atomic topology update: refuse over-budget fracture without partial mutation.
            if(!RebuildNodes(false)) {foreach(int i in candidate) {var face=Faces[i];face.cut=false;Faces[i]=face;} return 0;}
            CutFaceCount+=candidate.Count; TopologyRevision++; ReleaseHandle();
            // Retain each material side through cell-corner identities after fan duplication.
            // A nick must not detach a held peritoneum or weld its opposite crack lip.
            for(int i=materialHandles.Count-1;i>=0;i--)if(!BuildMaterialPatch(materialHandles[i]))materialHandles.RemoveAt(i);
            Dirty=true;
            if(HasAcceptedStep&&!ComputeNodalForces(acceptedForces,false)) {HasAcceptedStep=false;Array.Clear(acceptedForces,0,NodeCount);}
            return candidate.Count;
        }
        public static bool BladeIntersectsFace(Vector3 a,Vector3 b,Vector3 c,Vector3 p,Vector3 q,Vector3 r,float tolerance)
        {
            if(SegmentTriangle(a,b,p,q,r)||SegmentTriangle(b,c,p,q,r)||SegmentTriangle(c,a,p,q,r)||
               SegmentTriangle(p,q,a,b,c)||SegmentTriangle(q,r,a,b,c)||SegmentTriangle(r,p,a,b,c)) return true;
            // Coplanar/submillimeter contact: centroid must be close to the finite swept triangle.
            // This deliberately cannot fracture every face in an infinite cutting plane.
            Vector3 center=(p+q+r)/3; return (ClosestTriangle(center,a,b,c)-center).sqrMagnitude<=tolerance*tolerance;
        }
        public static bool SegmentTriangle(Vector3 start,Vector3 end,Vector3 a,Vector3 b,Vector3 c)
        {
            Vector3 direction=end-start,e1=b-a,e2=c-a,p=Vector3.Cross(direction,e2); float determinant=Vector3.Dot(e1,p);
            if(Mathf.Abs(determinant)<1e-12f) return false;
            float inverse=1/determinant; Vector3 t=start-a; float u=Vector3.Dot(t,p)*inverse;
            if(u<0 || u>1) return false;
            Vector3 q=Vector3.Cross(t,e1); float v=Vector3.Dot(direction,q)*inverse;
            if(v<0 || u+v>1) return false;
            float fraction=Vector3.Dot(e2,q)*inverse; return fraction>=0 && fraction<=1;
        }
        public static Vector3 ClosestTriangle(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        {
            Vector3 ab=b-a,ac=c-a,ap=p-a; float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
            if(d1<=0&&d2<=0)return a;
            Vector3 bp=p-b; float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp); if(d3>=0&&d4<=d3)return b;
            float vc=d1*d4-d3*d2; if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            Vector3 cp=p-c; float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;
            float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;if(va<=0&&(d4-d3)>=0&&(d5-d6)>=0)return b+(c-b)*((d4-d3)/((d4-d3)+(d5-d6)));
            float denom=va+vb+vc;if(Mathf.Abs(denom)<1e-18f)return a;
            return a+ab*(vb/denom)+ac*(vc/denom);
        }
        bool RebuildNodes(bool initial)
        {
            // Union cell corners around each original node through UNBROKEN incident faces.
            // A fracture splits only the connected material fans; crack tips can remain attached.
            int count=Cells.Length*4; var parent=new int[count];for(int i=0;i<count;i++)parent[i]=i;
            foreach(var face in Faces) if(face.neighbor>=0&&!face.cut)
            {
                UnionRoot(face.cell,face.neighbor,face.a,parent);UnionRoot(face.cell,face.neighbor,face.b,parent);UnionRoot(face.cell,face.neighbor,face.c,parent);
            }
            var map=new Dictionary<int,int>();var nextPositions=new List<Vector3>();var nextRest=new List<Vector3>();var nextVelocity=new List<Vector3>();var nextPins=new List<bool>();var mass=new List<float>();
            var nextBinding=new int[Cells.Length,4];var nextRoots=new List<int>();
            for(int cell=0;cell<Cells.Length;cell++)for(int corner=0;corner<4;corner++)
            {
                int set=Find(parent,cell*4+corner),root=Cells[cell].Vertex(corner);
                if(!map.TryGetValue(set,out int node))
                {
                    node=map.Count;if(node>=MaxNodes)return false;map.Add(set,node);
                    int old=initial?-1:binding[cell,corner];
                    nextPositions.Add(initial?Original[root]:positions[old]);nextRest.Add(Original[root]);nextVelocity.Add(initial?Vector3.zero:velocities[old]);nextPins.Add(originalPins[root]);nextRoots.Add(root);mass.Add(0);
                }
                nextBinding[cell,corner]=node;mass[node]+=Materials[Cells[cell].material].densityKgPerCubicMeter*restVolumes[cell]/4;
            }
            positions=nextPositions.ToArray();rest=nextRest.ToArray();velocities=nextVelocity.ToArray();pinned=nextPins.ToArray();previous=new Vector3[positions.Length];previousVelocities=new Vector3[positions.Length];inverseMass=new float[positions.Length];nodeMass=mass.ToArray();originalNodeFor=nextRoots.ToArray();
            acceptedForces=new Vector3[positions.Length];trialForces=new Vector3[positions.Length];TotalMass=0;
            for(int i=0;i<positions.Length;i++){TotalMass+=mass[i];inverseMass[i]=pinned[i]?0:1/mass[i];}
            for(int cell=0;cell<Cells.Length;cell++)for(int corner=0;corner<4;corner++)binding[cell,corner]=nextBinding[cell,corner];
            return true;
        }
        void UnionRoot(int a,int b,int root,int[] parent)
        {
            int ca=-1,cb=-1;for(int i=0;i<4;i++){if(Cells[a].Vertex(i)==root)ca=i;if(Cells[b].Vertex(i)==root)cb=i;}
            int ra=Find(parent,a*4+ca),rb=Find(parent,b*4+cb);if(ra!=rb)parent[rb]=ra;
        }
        static int Find(int[] parent,int node){while(parent[node]!=node){parent[node]=parent[parent[node]];node=parent[node];}return node;}

        public void Step(float seconds,Vector3 handleTarget,Vector3 acceleration)
        {
            LastStepAccepted=false;LastStepRetries=0;LastStepBacktracks=0;LastRejectedJacobian=float.PositiveInfinity;LastRejectedCell=-1;
            if(float.IsNaN(seconds)||seconds<=0||seconds>1f/30||!TissueCage.Finite(handleTarget)||!TissueCage.Finite(acceleration))return;
            Array.Copy(positions,previous,NodeCount);Array.Copy(velocities,previousVelocities,NodeCount);
            Vector3 target=Handle>=0?handleOrigin+Vector3.ClampMagnitude(handleTarget-handleOrigin,.02f):Vector3.zero;
            Vector3 increment=Handle>=0?Vector3.ClampMagnitude(target-acceptedHandleTarget,HandleSpeedMetersPerSecond*seconds):Vector3.zero;
            // Retry the same material-time trial with reduced target travel. No failed
            // attempt commits history, velocity or forces, and no numerical rejection
            // silently drops the user's grasp. Boundary targets remain prescribed.
            foreach(var grip in materialHandles)
            {
                Vector3 desired=grip.origin+Vector3.ClampMagnitude(grip.requestedTarget-grip.origin,.02f);
                grip.trialTarget=Vector3.ClampMagnitude(desired-grip.acceptedTarget,HandleSpeedMetersPerSecond*seconds);
            }
            int attempts=Handle>=0||materialHandles.Count>0?5:1;
            for(int attempt=0;attempt<attempts;attempt++)
            {
                LastStepRetries=attempt;
                Array.Copy(previous,positions,NodeCount);Array.Copy(previousVelocities,velocities,NodeCount);
                float retryScale=attempt==4?0:Mathf.Pow(.5f,attempt);
                Vector3 trialTarget=acceptedHandleTarget+increment*retryScale;
                if(!TryStep(seconds,trialTarget,acceleration,retryScale))continue;
                if(Handle>=0)acceptedHandleTarget=trialTarget;
                foreach(var grip in materialHandles)grip.acceptedTarget+=grip.trialTarget*retryScale;
                LastStepAccepted=true;AcceptedStepSequence++;return;
            }
            Array.Copy(previous,positions,NodeCount);Array.Copy(previousVelocities,velocities,NodeCount);
        }
        bool TryStep(float dt,Vector3 target,Vector3 acceleration,float predictionScale)
        {
            bool stabilize=Handle>=0||materialHandles.Count>0;
            for(int node=0;node<NodeCount&&stabilize;node++)if(pinned[node]&&(boundaryTargets[originalNodeFor[node]]-previous[node]).sqrMagnitude>1e-16f)stabilize=false;
            Array.Clear(lambdas,0,lambdas.Length);Array.Clear(attachmentLambdas,0,attachmentLambdas.Length);
            foreach(var grip in materialHandles)Array.Clear(grip.multipliers,0,grip.multipliers.Length);
            foreach(var history in histories)history.Prepare(dt);
            for(int i=0;i<NodeCount;i++)
            {
                if(pinned[i])continue;
                // Retry also damps the inertial predictor: a previously accepted
                // velocity must not force every smaller target trial to invert again.
                // This is numerical stabilization, not tissue viscosity.
                velocities[i]*=Mathf.Exp(-NumericVelocityDamping*dt)*predictionScale;positions[i]+=velocities[i]*dt+acceleration*(dt*dt);
            }
            for(int iteration=0;iteration<6;iteration++)
            {
                for(int cell=0;cell<Cells.Length;cell++)SolveCell(cell,dt);
                for(int grip=0;grip<handleNodes.Length;grip++)
                {
                    int node=handleNodes[grip];float alpha=HandleCompliance/(handleWeights[grip]*dt*dt);
                    Vector3 change=(-(positions[node]-target-handleOffsets[grip])-alpha*attachmentLambdas[grip])/(inverseMass[node]+alpha);
                    attachmentLambdas[grip]+=change;positions[node]+=inverseMass[node]*change;
                }
                foreach(var handle in materialHandles)
                {
                    Vector3 materialTarget=handle.acceptedTarget+handle.trialTarget*predictionScale;
                    for(int grip=0;grip<handle.nodes.Length;grip++)
                    {
                        int node=handle.nodes[grip];float alpha=HandleCompliance/(handle.weights[grip]*dt*dt);
                        Vector3 change=(-(positions[node]-materialTarget-handle.offsets[grip])-alpha*handle.multipliers[grip])/(inverseMass[node]+alpha);
                        handle.multipliers[grip]+=change;positions[node]+=inverseMass[node]*change;
                    }
                }
                for(int i=0;i<NodeCount;i++)if(pinned[i])positions[i]=boundaryTargets[originalNodeFor[i]];
                if(stabilize)for(int sweep=0;sweep<3;sweep++)for(int cell=0;cell<Cells.Length;cell++)SolveJacobianFloor(cell);
            }
            if(!ValidGeometry())
            {
                if(Handle<0&&materialHandles.Count==0)return false;
                // Backtrack the solved free-node displacement before advancing material
                // time. Thin stiff cells can invert during the unconstrained energy solve
                // even with a stationary predictor. Keep exact prescribed boundaries;
                // an impossible boundary therefore still rejects instead of being hidden.
                bool valid=false;
                for(int backtrack=1;backtrack<=8;backtrack++)
                {
                    for(int node=0;node<NodeCount;node++)if(!pinned[node])positions[node]=previous[node]+(positions[node]-previous[node])*.5f;
                    LastStepBacktracks=backtrack;
                    if(ValidGeometry()){valid=true;break;}
                }
                if(!valid)return false;
            }
            if(!ComputeNodalForces(trialForces,true))return false;
            for(int i=0;i<NodeCount;i++){velocities[i]=Vector3.ClampMagnitude((positions[i]-previous[i])/dt,.5f);Dirty|=(positions[i]-previous[i]).sqrMagnitude>1e-12f;}
            for(int cell=0;cell<Cells.Length;cell++){var f=Deformation(cell);histories[cell].Commit((f.Transpose().Multiply(f)-TissueTensor.Identity)*.5f);}
            Array.Copy(trialForces,acceptedForces,NodeCount);HasAcceptedStep=true;return true;
        }
        bool ValidGeometry()
        {
            for(int cell=0;cell<Cells.Length;cell++)
            {
                var f=Deformation(cell);var strain=(f.Transpose().Multiply(f)-TissueTensor.Identity)*.5f;
                if(float.IsNaN(f.Determinant)||float.IsInfinity(f.Determinant)||f.Determinant<=.02f||!TissueCage.Finite(f.x)||!TissueCage.Finite(f.y)||!TissueCage.Finite(f.z)||
                    !TissueCage.Finite(strain.x)||!TissueCage.Finite(strain.y)||!TissueCage.Finite(strain.z))
                {LastRejectedJacobian=f.Determinant;LastRejectedCell=cell;return false;}
            }
            return true;
        }
        bool ComputeNodalForces(Vector3[] forces,bool trial)
        {
            Array.Clear(forces,0,forces.Length);
            for(int cell=0;cell<Cells.Length;cell++)
            {
                var f=Deformation(cell);var strain=(f.Transpose().Multiply(f)-TissueTensor.Identity)*.5f;
                var material=Materials[Cells[cell].material];
                var stress=trial?histories[cell].Stress(strain,material):histories[cell].CommittedStress(material);
                var gradient=f.Multiply(stress).Multiply(restInverse[cell].Transpose())*restVolumes[cell];
                if(!TissueCage.Finite(gradient.x)||!TissueCage.Finite(gradient.y)||!TissueCage.Finite(gradient.z))return false;
                forces[binding[cell,0]]+=gradient.x+gradient.y+gradient.z;
                forces[binding[cell,1]]-=gradient.x;forces[binding[cell,2]]-=gradient.y;forces[binding[cell,3]]-=gradient.z;
            }
            foreach(var force in forces)if(!TissueCage.Finite(force))return false;
            return true;
        }
        TissueTensor Deformation(int cell)
        {
            int a=binding[cell,0];return new TissueTensor(positions[binding[cell,1]]-positions[a],positions[binding[cell,2]]-positions[a],positions[binding[cell,3]]-positions[a]).Multiply(restInverse[cell]);
        }
        // Numerical noninversion inequality for interactive grasps. This is not a
        // calibrated compressibility/fracture law; the constitutive energy is unchanged.
        void SolveJacobianFloor(int cell)
        {
            var f=Deformation(cell);float determinant=f.Determinant;
            if(float.IsNaN(determinant)||float.IsInfinity(determinant)||determinant>=.2f)return;
            var cofactor=new TissueTensor(Vector3.Cross(f.y,f.z),Vector3.Cross(f.z,f.x),Vector3.Cross(f.x,f.y));
            var gradient=cofactor.Multiply(restInverse[cell].Transpose());
            Vector3 g1=gradient.x,g2=gradient.y,g3=gradient.z,g0=-g1-g2-g3;
            int a=binding[cell,0],b=binding[cell,1],c=binding[cell,2],d=binding[cell,3];
            float denom=inverseMass[a]*g0.sqrMagnitude+inverseMass[b]*g1.sqrMagnitude+inverseMass[c]*g2.sqrMagnitude+inverseMass[d]*g3.sqrMagnitude;
            if(!(denom>1e-20f)||float.IsInfinity(denom))return;
            float change=(.2f-determinant)/denom;
            positions[a]+=inverseMass[a]*change*g0;positions[b]+=inverseMass[b]*change*g1;positions[c]+=inverseMass[c]*change*g2;positions[d]+=inverseMass[d]*change*g3;
        }
        void SolveCell(int cell,float dt)
        {
            var f=Deformation(cell);var strain=(f.Transpose().Multiply(f)-TissueTensor.Identity)*.5f;
            if(!TissueCage.Finite(strain.x)||!TissueCage.Finite(strain.y)||!TissueCage.Finite(strain.z))return;
            var material=Materials[Cells[cell].material];float energy=histories[cell].Energy(strain,material)*restVolumes[cell];
            if(energy<=1e-16f||float.IsNaN(energy)||float.IsInfinity(energy))return;
            float constraint=Mathf.Sqrt(2*energy);
            var gradient=f.Multiply(histories[cell].Stress(strain,material)).Multiply(restInverse[cell].Transpose())*(restVolumes[cell]/constraint);
            Vector3 g1=gradient.x,g2=gradient.y,g3=gradient.z,g0=-g1-g2-g3;
            int a=binding[cell,0],b=binding[cell,1],c=binding[cell,2],d=binding[cell,3];
            float alpha=1/(dt*dt),denom=inverseMass[a]*g0.sqrMagnitude+inverseMass[b]*g1.sqrMagnitude+inverseMass[c]*g2.sqrMagnitude+inverseMass[d]*g3.sqrMagnitude+alpha;
            float change=(-constraint-alpha*lambdas[cell])/denom;lambdas[cell]+=change;
            positions[a]+=inverseMass[a]*change*g0;positions[b]+=inverseMass[b]*change*g1;positions[c]+=inverseMass[c]*change*g2;positions[d]+=inverseMass[d]*change*g3;
        }
        public void Reset()
        {
            for(int i=0;i<Faces.Length;i++){var face=Faces[i];face.cut=false;Faces[i]=face;}
            foreach(var history in histories)history.Reset();
            Array.Copy(Original,boundaryTargets,Original.Length);HasAcceptedStep=false;TopologyRevision++;
            CutFaceCount=0;LastStepAccepted=false;LastStepRetries=0;LastStepBacktracks=0;LastRejectedJacobian=float.PositiveInfinity;LastRejectedCell=-1;ReleaseHandle();ReleaseMaterialHandles();if(!RebuildNodes(true)) throw new ArgumentException("Initial material connectivity exceeds tissue node budget");Dirty=true;
        }
        public void Freeze(){ReleaseHandle();ReleaseMaterialHandles();Array.Clear(velocities,0,velocities.Length);}
        public void MarkCommitted()=>Dirty=false;

        // Query current owned material geometry directly, including both open cut sides.
        // No Mesh.vertices/triangles copies and no stale 30 Hz render-mesh dependency.
        public float SurfaceDistanceSquared(Vector3 point,float maximumDistance)
        {
            if(!TissueCage.Finite(point)||float.IsNaN(maximumDistance)||float.IsInfinity(maximumDistance)||maximumDistance<=0)return float.PositiveInfinity;
            float nearest=maximumDistance*maximumDistance;
            foreach(var face in Faces)
            {
                if(face.neighbor>=0&&!face.cut)continue;
                nearest=Mathf.Min(nearest,FaceDistanceSquared(face,face.cell,point));
                if(face.cut&&face.neighbor>=0)nearest=Mathf.Min(nearest,FaceDistanceSquared(face,face.neighbor,point));
            }
            return nearest;
        }
        float FaceDistanceSquared(Face face,int cell,Vector3 point)
        {
            return (ClosestTriangle(point,positions[BoundRoot(cell,face.a)],positions[BoundRoot(cell,face.b)],positions[BoundRoot(cell,face.c)])-point).sqrMagnitude;
        }

        // Both sides of each broken face become a real interior surface with outward winding.
        public void WriteSurface(Mesh mesh)
        {
            var vertices=surfaceVertices;vertices.Clear();var submesh=surfaceTriangles;for(int i=0;i<submesh.Length;i++)submesh[i].Clear();
            foreach(var face in Faces)
            {
                if(face.neighbor>=0&&!face.cut)continue;
                AddSurface(face,face.cell,vertices,submesh);if(face.cut&&face.neighbor>=0)AddSurface(face,face.neighbor,vertices,submesh);
            }
            mesh.Clear();mesh.SetVertices(vertices);mesh.subMeshCount=submesh.Length;
            for(int i=0;i<submesh.Length;i++)mesh.SetTriangles(submesh[i],i);mesh.RecalculateNormals();mesh.RecalculateBounds();Dirty=false;
        }
        void AddSurface(Face face,int cell,List<Vector3> vertices,List<int>[] indices)
        {
            Vector3 a=positions[BoundRoot(cell,face.a)],b=positions[BoundRoot(cell,face.b)],c=positions[BoundRoot(cell,face.c)];
            Vector3 center=Vector3.zero;for(int v=0;v<4;v++)center+=positions[binding[cell,v]]*.25f;
            if(Vector3.Dot(Vector3.Cross(b-a,c-a),center-a)>0){var tmp=b;b=c;c=tmp;}
            int start=vertices.Count;vertices.Add(a);vertices.Add(b);vertices.Add(c);
            var triangles=indices[Cells[cell].material];triangles.Add(start);triangles.Add(start+1);triangles.Add(start+2);
        }
    }
}
