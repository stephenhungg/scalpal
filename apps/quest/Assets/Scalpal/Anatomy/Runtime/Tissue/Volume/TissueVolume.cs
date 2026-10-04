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
        readonly TissueTensor[] restInverse;
        readonly float[] restVolumes, lambdas;
        readonly MaxwellHistory[] histories;
        readonly int[,] binding;
        Vector3[] positions, rest, previous, velocities;
        float[] inverseMass;
        bool[] pinned;
        public Vector3[] Positions => positions;
        public Vector3[] Rest => rest;
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
            Original=(Vector3[])nodes.Clone(); Cells=(Cell[])cells.Clone(); Materials=(VolumeMaterial[])materials.Clone(); originalPins=(bool[])pins.Clone();
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
            Faces=BuildFaces(cells); Reset();
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
        public bool BeginHandle(Vector3 point,float maximumDistance)
        {
            if(!TissueCage.Finite(point)||!(maximumDistance>0)||float.IsInfinity(maximumDistance)){ReleaseHandle();return false;}
            Handle=-1; float distance=maximumDistance*maximumDistance;
            for(int i=0;i<NodeCount;i++) if(!pinned[i])
            {
                float d=(positions[i]-point).sqrMagnitude;
                if(d<distance) {distance=d;Handle=i;}
            }
            return Handle>=0;
        }
        public void ReleaseHandle() => Handle=-1;
        public Vector3 HandlePosition => Handle<0?Vector3.zero:positions[Handle];

        // A finite triangle swept by the real blade breaks intersected shared cell faces.
        public int CutSweep(Vector3 a,Vector3 b,Vector3 c,float contactTolerance)
        {
            if(!TissueCage.Finite(a)||!TissueCage.Finite(b)||!TissueCage.Finite(c)||Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f) return 0;
            if(float.IsNaN(contactTolerance)||contactTolerance<0 || contactTolerance>.003f || CutFaceCount>=MaxCutFaces) return 0;
            var candidate=new List<int>();
            for(int i=0;i<Faces.Length;i++)
            {
                var face=Faces[i]; if(face.neighbor<0 || face.cut) continue;
                Vector3 p=positions[BoundRoot(face.cell,face.a)],q=positions[BoundRoot(face.cell,face.b)],r=positions[BoundRoot(face.cell,face.c)];
                if(BladeIntersectsFace(a,b,c,p,q,r,contactTolerance)) candidate.Add(i);
            }
            if(candidate.Count==0 || CutFaceCount+candidate.Count>MaxCutFaces) return 0;
            foreach(int i in candidate) {var face=Faces[i];face.cut=true;Faces[i]=face;}
            // Atomic topology update: refuse over-budget fracture without partial mutation.
            if(!RebuildNodes(false)) {foreach(int i in candidate) {var face=Faces[i];face.cut=false;Faces[i]=face;} return 0;}
            CutFaceCount+=candidate.Count; ReleaseHandle(); Dirty=true; return candidate.Count;
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
            var nextBinding=new int[Cells.Length,4];
            for(int cell=0;cell<Cells.Length;cell++)for(int corner=0;corner<4;corner++)
            {
                int set=Find(parent,cell*4+corner),root=Cells[cell].Vertex(corner);
                if(!map.TryGetValue(set,out int node))
                {
                    node=map.Count;if(node>=MaxNodes)return false;map.Add(set,node);
                    int old=initial?-1:binding[cell,corner];
                    nextPositions.Add(initial?Original[root]:positions[old]);nextRest.Add(Original[root]);nextVelocity.Add(initial?Vector3.zero:velocities[old]);nextPins.Add(originalPins[root]);mass.Add(0);
                }
                nextBinding[cell,corner]=node;mass[node]+=Materials[Cells[cell].material].densityKgPerCubicMeter*restVolumes[cell]/4;
            }
            positions=nextPositions.ToArray();rest=nextRest.ToArray();velocities=nextVelocity.ToArray();pinned=nextPins.ToArray();previous=new Vector3[positions.Length];inverseMass=new float[positions.Length];TotalMass=0;
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
            if(float.IsNaN(seconds)||seconds<=0||seconds>1f/30||!TissueCage.Finite(handleTarget)||!TissueCage.Finite(acceleration)) {ReleaseHandle();return;}
            float dt=seconds;Array.Clear(lambdas,0,lambdas.Length);
            foreach(var history in histories)history.Prepare(dt);
            for(int i=0;i<NodeCount;i++)
            {
                previous[i]=positions[i];if(pinned[i])continue;
                velocities[i]*=Mathf.Exp(-NumericVelocityDamping*dt);positions[i]+=velocities[i]*dt+acceleration*(dt*dt);
            }
            for(int iteration=0;iteration<6;iteration++)
            {
                for(int cell=0;cell<Cells.Length;cell++)SolveCell(cell,dt);
                if(Handle>=0)positions[Handle]=rest[Handle]+Vector3.ClampMagnitude(handleTarget-rest[Handle],.02f);
                for(int i=0;i<NodeCount;i++)if(pinned[i])positions[i]=rest[i];
            }
            bool invalid=false;
            for(int cell=0;cell<Cells.Length;cell++)
            {
                var f=Deformation(cell);var strain=(f.Transpose().Multiply(f)-TissueTensor.Identity)*.5f;
                if(float.IsNaN(f.Determinant)||float.IsInfinity(f.Determinant)||f.Determinant<=.02f||!TissueCage.Finite(f.x)||!TissueCage.Finite(f.y)||!TissueCage.Finite(f.z)||
                    !TissueCage.Finite(strain.x)||!TissueCage.Finite(strain.y)||!TissueCage.Finite(strain.z)){invalid=true;break;}
            }
            if(invalid)
            {
                // Roll back a failed timestep, preserving the existing cut topology and mass.
                Array.Copy(previous,positions,NodeCount);Array.Clear(velocities,0,NodeCount);ReleaseHandle();Dirty=true;return;
            }
            for(int i=0;i<NodeCount;i++){velocities[i]=Vector3.ClampMagnitude((positions[i]-previous[i])/dt,.5f);Dirty|=(positions[i]-previous[i]).sqrMagnitude>1e-12f;}
            for(int cell=0;cell<Cells.Length;cell++){var f=Deformation(cell);histories[cell].Commit((f.Transpose().Multiply(f)-TissueTensor.Identity)*.5f);}
        }
        TissueTensor Deformation(int cell)
        {
            int a=binding[cell,0];return new TissueTensor(positions[binding[cell,1]]-positions[a],positions[binding[cell,2]]-positions[a],positions[binding[cell,3]]-positions[a]).Multiply(restInverse[cell]);
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
            CutFaceCount=0;ReleaseHandle();if(!RebuildNodes(true)) throw new ArgumentException("Initial material connectivity exceeds tissue node budget");Dirty=true;
        }
        public void Freeze(){ReleaseHandle();Array.Clear(velocities,0,velocities.Length);}
        public void MarkCommitted()=>Dirty=false;

        // Both sides of each broken face become a real interior surface with outward winding.
        public void WriteSurface(Mesh mesh)
        {
            var vertices=new List<Vector3>();var submesh=new List<int>[Materials.Length];for(int i=0;i<submesh.Length;i++)submesh[i]=new List<int>();
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
