using System;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Uses actual node fans/current surface, rather than a cut-count or controller-distance
    // proxy for an exposed membrane. The Surgery coupling gate owns the clinical milestones.
    public static class NativeTissueInterfaceValidation
    {
        static int checks;
        static readonly Vector3 A=new Vector3(-.04f,0,.018f);
        static readonly Vector3 B=new Vector3(.04f,0,.018f);
        static readonly Vector3 C=new Vector3(.04f,0,.028f);
        const float Margin=.035f;
        static void Require(bool ok,string message)
        {checks++;if(!ok)throw new InvalidOperationException("Tissue interface: "+message);}

        [MenuItem("Scalpal/Quest/Validate Tissue Interfaces")]
        public static void Run()
        {
            checks=0;
            ValidateExposure();
            ValidateAtomicBudget();
            ValidateLegacyCut();
            Debug.Log("SCALPAL_TISSUE_INTERFACE_VALIDATION_OK checks="+checks+" synthetic=true physicalHeadset=false");
        }
        static void ValidateExposure()
        {
            var volume=TissueVolumeFactory.OpenAbdominalWall();
            int initialNodes=volume.NodeCount,revision=volume.TopologyRevision;
            var query=new Vector3(0,.003f,.027f);
            Require(!volume.TryMaterialContact("peritoneum",query,.0005f,out _),"intact anterior membrane is not an exposed face");
            Require(volume.TryMaterialContact("peritoneum",query,.002f,out var posterior)&&Mathf.Abs(posterior.z-.028f)<1e-6f,
                "before splitting a nearby query acquires the posterior membrane, reproducing the original failure");
            double mass=NodeMass(volume),currentVolume=CurrentVolume(volume);
            int peritoneumCuts=volume.CutFacesForMaterial("peritoneum");
            Require(volume.SplitMaterialSweep("muscle","peritoneum",A,B,C,.0025f,Margin)>0,"finite split publishes actual muscle fractures");
            Require(volume.TopologyRevision==revision+1&&volume.NodeCount>initialNodes,"joint split/separation publishes one real topology revision");
            Require(volume.SeparatedFaceCount>0&&volume.SeparatedFacesForMaterials("muscle","peritoneum")==volume.SeparatedFaceCount,
                "only the intended underlying interface is separated");
            Require(volume.CutFacesForMaterial("peritoneum")==peritoneumCuts&&peritoneumCuts==0,"interface exposure cannot fabricate a peritoneum incision");
            Require(volume.CutFacesForMaterial("skin")==0&&volume.CutFacesForMaterial("fat")==0&&volume.CutFacesForMaterial("fascia")==0,
                "split does not injure preceding materials");
            Require(volume.TryMaterialContact("peritoneum",query,.0005f,out var anterior)&&Mathf.Abs(anterior.z-.027f)<1e-6f,
                "forceps can now acquire the actual anterior membrane at 27 mm, not the posterior at 28 mm");
            Require(volume.SurfaceDistanceSquared(query,.001f)<1e-12f,"separated anterior interface participates in current surface distance");
            int separated=0,broken=0,farAttached=0,centralReleased=0;
            foreach(var face in volume.Faces)
            {
                if(face.cut)broken++;
                if(face.separated)
                {
                    separated++;
                    Require(!face.cut&&Interface(volume,face,"muscle","peritoneum"),"exposure flag is distinct from a cut and belongs to the selected material pair");
                    var center=(volume.Original[face.a]+volume.Original[face.b]+volume.Original[face.c])/3;
                    Require((TissueVolume.ClosestTriangle(center,A,B,C)-center).sqrMagnitude<=Margin*Margin+1e-9f,
                        "each separated face centroid is bounded by the finite triangle support, never an infinite cutting plane");
                    if(Mathf.Abs(center.x)<.015f&&Mathf.Abs(center.y)<.01f)
                    {
                        foreach(int root in new[]{face.a,face.b,face.c})
                            if(Mathf.Abs(volume.Original[root].x)<.02f&&Mathf.Abs(volume.Original[root].y)<.01f)
                            {
                                Require(Bound(volume,face.cell,root)!=Bound(volume,face.neighbor,root),"interior released membrane nodes are independent of muscle nodes");
                                centralReleased++;
                            }
                    }
                }
                else if(Interface(volume,face,"muscle","peritoneum"))
                {
                    var center=(volume.Original[face.a]+volume.Original[face.b]+volume.Original[face.c])/3;
                    if((TissueVolume.ClosestTriangle(center,A,B,C)-center).sqrMagnitude>Margin*Margin)
                    {
                        foreach(int root in new[]{face.a,face.b,face.c})
                            Require(Bound(volume,face.cell,root)==Bound(volume,face.neighbor,root),"surrounding interfaces retain physical attachments");
                        farAttached++;
                    }
                }
            }
            Require(separated==volume.SeparatedFaceCount&&broken==volume.CutFaceCount&&centralReleased>0&&farAttached>0,
                "independent flags/bindings confirm released centre and retained surrounding attachment");
            Require(Near(NodeMass(volume),mass)&&Near(CurrentVolume(volume),currentVolume),"actual active nodal mass sum and current bound-cell volume survive separation");
            var mesh=new Mesh();
            try
            {
                volume.WriteSurface(mesh);
                var vertices=mesh.vertices;var triangles=mesh.GetTriangles(Array.FindIndex(volume.Materials,m=>m.id=="peritoneum"));
                float distance=float.PositiveInfinity;
                for(int i=0;i<triangles.Length;i+=3)
                    distance=Mathf.Min(distance,(TissueVolume.ClosestTriangle(query,vertices[triangles[i]],vertices[triangles[i+1]],vertices[triangles[i+2]])-query).sqrMagnitude);
                Require(distance<1e-12f,"rendered peritoneum submesh includes the exposed anterior face");
            }
            finally{UnityEngine.Object.DestroyImmediate(mesh);}
            int sameNodes=volume.NodeCount,sameRevision=volume.TopologyRevision,sameSeparations=volume.SeparatedFaceCount;
            Require(volume.SplitMaterialSweep("muscle","peritoneum",A,B,C,.0025f,Margin)==0&&volume.NodeCount==sameNodes&&
                volume.TopologyRevision==sameRevision&&volume.SeparatedFaceCount==sameSeparations,"duplicate split does not release more interfaces or publish another revision");
            Require(volume.BeginMaterialHandle(7,"peritoneum",query,.0005f,.012f),"actual anterior membrane acquires a physical material attachment");
            Require(volume.TryMaterialHandlePosition(7,out var anchor)&&Mathf.Abs(anchor.z-.027f)<1e-6f,"material handle uses the anterior cell corners");
            Require(volume.SetMaterialHandleTarget(7,anchor-Vector3.forward*.005f),"membrane attachment accepts a finite outward request");
            for(int i=0;i<30;i++)volume.Step(1f/90,Vector3.zero,Vector3.zero);
            Require(volume.TryMaterialHandlePosition(7,out var lifted)&&lifted.z<anchor.z-.0005f,"released membrane has actual accepted outward motion, beyond the old welded-interface fixture");
            PositiveGeometry(volume);
            Require(volume.CutFacesForMaterial("peritoneum")==0,"tenting does not cut the membrane");
            volume.Reset();
            Require(volume.NodeCount==initialNodes&&volume.CutFaceCount==0&&volume.SeparatedFaceCount==0&&volume.MaterialHandleCount==0,"reset restores welded topology and clears grips");
            Require(!volume.TryMaterialContact("peritoneum",query,.0005f,out _),"reset removes exposed anterior membrane contact");
            Require(Near(NodeMass(volume),mass)&&Near(CurrentVolume(volume),currentVolume),"reset restores active mass and actual cell geometry");
            foreach(var face in volume.Faces)Require(!face.cut&&!face.separated,"reset clears both distinct topology flags");
        }
        static void ValidateAtomicBudget()
        {
            var source=TissueVolumeFactory.OpenAbdominalWall();var pins=new bool[source.Original.Length];
            for(int i=0;i<pins.Length;i++)pins[i]=Mathf.Abs(source.Original[i].x)>=.0799f||Mathf.Abs(source.Original[i].y)>=.0499f;
            var volume=new TissueVolume(source.Original,source.Cells,source.Materials,pins,1);
            Require(volume.BeginMaterialHandle(1,"skin",Vector3.zero,.001f),"over-budget fixture starts with a live grip");
            var positions=(Vector3[])volume.Positions.Clone();int nodes=volume.NodeCount,revision=volume.TopologyRevision;
            Require(volume.SplitMaterialSweep("muscle","peritoneum",A,B,C,.0025f,Margin)==0,"combined fracture/separation exceeds a tiny explicit budget and refuses");
            Require(volume.NodeCount==nodes&&volume.TopologyRevision==revision&&volume.CutFaceCount==0&&volume.SeparatedFaceCount==0&&volume.MaterialHandleCount==1,
                "budget refusal leaves both topology counters, binding size, revision and existing grip unchanged");
            for(int i=0;i<nodes;i++)Require(volume.Positions[i].Equals(positions[i]),"budget refusal preserves current nodal positions exactly");
            foreach(var face in volume.Faces)Require(!face.cut&&!face.separated,"budget refusal leaves no partial interface or cut flags");
            foreach(var bad in new[]{float.NaN,float.PositiveInfinity,-.01f,0,.051f})
                Require(source.SplitMaterialSweep("muscle","peritoneum",A,B,C,.0025f,bad)==0,"invalid interface margin refuses before mutation");
            Require(source.SplitMaterialSweep("muscle","unknown",A,B,C,.0025f,Margin)==0&&
                source.SplitMaterialSweep("muscle","skin",A,B,C,.0025f,Margin)==0,"unknown/nonadjacent underlying material refuses before mutation");
            Require(source.SplitMaterialSweep("muscle","peritoneum",A+Vector3.right,B+Vector3.right,C+Vector3.right,.0025f,Margin)==0&&source.SeparatedFaceCount==0,
                "out-of-volume finite split cannot detach any interface");
        }
        static void ValidateLegacyCut()
        {
            var volume=TissueVolumeFactory.OpenAbdominalWall();
            Require(volume.FractureMaterialSweep("muscle",A,B,C,.0025f)>0&&volume.SeparatedFaceCount==0,"existing material fracture preserves welded interfaces");
            Require(volume.CutFacesForMaterial("peritoneum")==0&&!volume.TryMaterialContact("peritoneum",new Vector3(0,.003f,.027f),.0005f,out _),
                "ordinary muscle cut does not silently invoke interface separation");
        }
        static bool Interface(TissueVolume volume,TissueVolume.Face face,string first,string second)=>face.neighbor>=0&&
            (volume.Materials[volume.Cells[face.cell].material].id==first&&volume.Materials[volume.Cells[face.neighbor].material].id==second||
             volume.Materials[volume.Cells[face.cell].material].id==second&&volume.Materials[volume.Cells[face.neighbor].material].id==first);
        static int Bound(TissueVolume volume,int cell,int root)
        {
            for(int corner=0;corner<4;corner++)if(volume.Cells[cell].Vertex(corner)==root)return volume.NodeFor(cell,corner);
            throw new InvalidOperationException("missing face corner");
        }
        static double NodeMass(TissueVolume volume)
        {double total=0;for(int i=0;i<volume.NodeCount;i++)total+=volume.NodeMass(i);return total;}
        static double CurrentVolume(TissueVolume volume)
        {
            double sum=0;
            for(int i=0;i<volume.Cells.Length;i++)
            {
                var p=volume.Positions[volume.NodeFor(i,0)];
                sum+=Math.Abs(Vector3.Dot(volume.Positions[volume.NodeFor(i,1)]-p,Vector3.Cross(volume.Positions[volume.NodeFor(i,2)]-p,volume.Positions[volume.NodeFor(i,3)]-p)))/6.0;
            }
            return sum;
        }
        static bool Near(double a,double b)=>Math.Abs(a-b)<=Math.Max(1e-11,Math.Abs(a)*2e-6);
        static void PositiveGeometry(TissueVolume volume)
        {
            for(int i=0;i<volume.Cells.Length;i++)
            {
                var cell=volume.Cells[i];var p=volume.Positions[volume.NodeFor(i,0)];
                var current=new TissueTensor(volume.Positions[volume.NodeFor(i,1)]-p,volume.Positions[volume.NodeFor(i,2)]-p,volume.Positions[volume.NodeFor(i,3)]-p);
                var reference=new TissueTensor(volume.Original[cell.b]-volume.Original[cell.a],volume.Original[cell.c]-volume.Original[cell.a],volume.Original[cell.d]-volume.Original[cell.a]);
                float j=current.Multiply(reference.Inverse()).Determinant;
                Require(float.IsFinite(j)&&j>.02f,"actual tented cell geometry stays above the unchanged Jacobian rejection floor");
            }
        }
    }
}
