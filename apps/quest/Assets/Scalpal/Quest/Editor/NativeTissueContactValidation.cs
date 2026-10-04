using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic closed two-body geometry. Does not establish actual organ contact or Quest performance.
    public static class NativeTissueContactValidation
    {
        static int checks;
        static void Assert(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException("Tissue contact: "+message);}
        static Vector3[] Copy(TissueCage cage)=>(Vector3[])cage.Positions.Clone();
        static float Motion(Vector3[] before,TissueCage cage){float maximum=0;for(int i=0;i<8;i++)maximum=Mathf.Max(maximum,Vector3.Distance(before[i],cage.Positions[i]));return maximum;}
        static void Positive(TissueCage cage)
        {
            int[,] tet={{0,1,2,4},{1,2,3,7},{1,4,5,7},{2,4,6,7},{1,2,4,7}};
            for(int t=0;t<5;t++)
            {
                float Signed(Vector3[] p)=>Vector3.Dot(p[tet[t,1]]-p[tet[t,0]],Vector3.Cross(p[tet[t,2]]-p[tet[t,0]],p[tet[t,3]]-p[tet[t,0]]));
                Assert(Signed(cage.Rest)*Signed(cage.Positions)>0,"contact never inverts material cells");
            }
            for(int i=0;i<8;i++)Assert(TissueCage.Finite(cage.Positions[i])&&Vector3.Distance(cage.Positions[i],cage.Rest[i])<=cage.Preset.maxDisplacement+.00001f,"contact displacement is finite and bounded in source meters");
            for(int i=0;i<8;i++)if(cage.IsPinned(i))Assert(cage.Positions[i]==cage.Rest[i],"posterior attachments remain fixed");
        }
        [MenuItem("Scalpal/Quest/Validate Tissue Contact")]
        public static void Run()
        {
            NativeSessionBuild.Validate();
            checks=0;
            var root=new GameObject("SyntheticTwoBodyContact");
            Mesh sourceA=null,sourceB=null;
            try
            {
                var a=Body(root,"A",out sourceA);var b=Body(root,"B",out sourceB);
                var originalA=sourceA.vertices;var originalB=sourceB.vertices;var originalTrianglesA=sourceA.triangles;
                using var solver=new TissueContactSolver();solver.Initialize(new[]{a,b});
                Assert(solver.SupportedBodies==2,"closed outward surface probes initialize both bodies");
                a.transform.localPosition=new Vector3(-.019f,0,0);b.transform.localPosition=new Vector3(.019f,.005f,-.005f);
                var beforeA=Copy(a.Cage);var beforeB=Copy(b.Cage);
                solver.Solve(false);
                Assert(solver.AppliedPairs==0&&solver.MaximumResidualPenetrationMeters==0&&Motion(beforeA,a.Cage)==0&&Motion(beforeB,b.Cage)==0,"registration failure forbids contact corrections and clears diagnostics");
                solver.Solve(true);
                Assert(solver.AppliedPairs==1,"overlapping actual surfaces produce a bounded two-way correction");
                Assert(Motion(beforeA,a.Cage)>0&&Motion(beforeB,b.Cage)>0,"both participating cages respond rather than moving only one visual mesh");
                Assert(solver.MaximumSamplingGapMeters>=TissueContactSolver.ContactPaddingMeters&&!float.IsNaN(solver.MaximumSamplingGapMeters)&&!float.IsInfinity(solver.MaximumSamplingGapMeters),"surface sampling exposes conservative world-meter gap");
                Positive(a.Cage);Positive(b.Cage);
                a.CommitSurface();b.CommitSurface();
                Assert(a.GetComponent<MeshFilter>().sharedMesh==a.GetComponent<MeshCollider>().sharedMesh&&b.GetComponent<MeshFilter>().sharedMesh==b.GetComponent<MeshCollider>().sharedMesh,"contact geometry and rendered private meshes commit together");
                var displayed=a.GetComponent<MeshFilter>().sharedMesh.vertices;
                Assert(AnyChanged(originalA,displayed),"contact correction reaches the rendered surface");
                for(int i=0;i<originalA.Length;i++)Assert(sourceA.vertices[i]==originalA[i],"contact leaves first source asset unchanged");
                for(int i=0;i<originalB.Length;i++)Assert(sourceB.vertices[i]==originalB[i],"contact leaves second source asset unchanged");
                Assert(Equal(originalTrianglesA,sourceA.triangles),"contact cannot alter source topology");
                Assert(!float.IsNaN(solver.MaximumResidualPenetrationMeters)&&solver.MaximumResidualPenetrationMeters>=0,"residual reports unresolved sampled penetration honestly");

                a.ResetTissue();b.ResetTissue();b.transform.localPosition=Vector3.right*.08f;
                beforeA=Copy(a.Cage);beforeB=Copy(b.Cage);solver.Solve(true);
                Assert(solver.AppliedPairs==0&&solver.MaximumResidualPenetrationMeters==0&&Motion(beforeA,a.Cage)==0&&Motion(beforeB,b.Cage)==0,"separated bodies do not acquire artificial contact forces");
                a.transform.localPosition=new Vector3(-.019f,0,0);b.transform.localPosition=new Vector3(.019f,.005f,-.005f);
                root.transform.SetPositionAndRotation(new Vector3(2,.4f,-1),Quaternion.Euler(0,37,0));root.transform.localScale=Vector3.one*1.5f;
                solver.Solve(true);Assert(solver.AppliedPairs==1,"shared rotation translation and uniform metric scale preserve world-space contact");Positive(a.Cage);Positive(b.Cage);
                a.ResetTissue();b.ResetTissue();b.transform.localScale=new Vector3(1,2,1);
                beforeA=Copy(a.Cage);beforeB=Copy(b.Cage);solver.Solve(true);
                Assert(solver.AppliedPairs==0&&Motion(beforeA,a.Cage)==0&&Motion(beforeB,b.Cage)==0,"unsupported nonuniform frame fails closed instead of mixing units");
                b.transform.localScale=Vector3.one;
                a.enabled=false;solver.Solve(true);Assert(solver.AppliedPairs==0,"disabled tissue cannot participate in contact");a.enabled=true;

                ValidateImportUnits(root);
                var cage=new TissueCage(new Bounds(Vector3.zero,new Vector3(.02f,.02f,.002f)),TissuePreset.Bowel);
                var intact=Copy(cage);
                Assert(!cage.ApplyContact(cage.Rest[4],Vector3.right*.001f)&&Motion(intact,cage)==0,"fully pinned contact is refused without mutation");
                Assert(!cage.ApplyContact(cage.Rest[0],Vector3.forward*.004f)&&Motion(intact,cage)==0,"inverting contact candidate rolls back atomically");
                Assert(!cage.ApplyContact(cage.Rest[0],new Vector3(float.NaN,0,0))&&!cage.ApplyContact(cage.Rest[0],Vector3.right*.006f)&&Motion(intact,cage)==0,"nonfinite or excessive correction cannot mutate cage");
                a.RestoreSource();b.RestoreSource();Assert(a.GetComponent<MeshFilter>().sharedMesh==sourceA&&b.GetComponent<MeshCollider>().sharedMesh==sourceB,"disposing contact mechanics restores original geometry");
                solver.Solve(true);Assert(solver.AppliedPairs==0,"disposed cages cannot receive stale contact corrections");
                var open=Body(root,"Open",out var openSource);
                var triangles=openSource.triangles;Array.Resize(ref triangles,triangles.Length-3);openSource.triangles=triangles;
                solver.Initialize(new[]{open});Assert(solver.SupportedBodies==0,"open geometry is rejected before signed contact approximation");
                open.RestoreSource();UnityEngine.Object.DestroyImmediate(openSource);
            }
            finally
            {
                foreach(var body in root.GetComponentsInChildren<DeformableTissue>(true))body.RestoreSource();
                UnityEngine.Object.DestroyImmediate(root);if(sourceA)UnityEngine.Object.DestroyImmediate(sourceA);if(sourceB)UnityEngine.Object.DestroyImmediate(sourceB);
            }
            ValidateContactVelocityAndMotion();
            ValidateTerminalCaps();
            ValidateSmallGeometry();
            ValidateAuthoredAttachments();
            AuditNativeMeshes();
            Debug.Log("SCALPAL_NATIVE_TISSUE_CONTACT_VALIDATION_OK checks="+checks+" synthetic surface probes/atomic two-way correction; no physical organ or headset evidence");
        }
        static void ValidateContactVelocityAndMotion()
        {
            var cage=new TissueCage(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.04f)),TissuePreset.Bowel);
            var velocity=(Vector3[])typeof(TissueCage).GetField("velocity",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(cage);
            velocity[0]=new Vector3(-.1f,.05f,.03f);velocity[1]=new Vector3(.07f,.08f,.09f);
            Assert(cage.ApplyContact(cage.Rest[0],Vector3.right*.0001f),"velocity regression applies real safe contact correction");
            Assert(cage.Velocity(0).x==0&&Mathf.Abs(cage.Velocity(0).y-.05f)<1e-7f&&Mathf.Abs(cage.Velocity(0).z-.03f)<1e-7f,"contact removes closing velocity but preserves tangential motion");
            Assert(cage.Velocity(1)==new Vector3(.07f,.08f,.09f),"contact cannot zero untouched-node velocity");
            var root=new GameObject("ContinuousSampledContactRegression");Mesh ma=null,mb=null;
            try
            {
                var a=Body(root,"MovingA",out ma);var b=Body(root,"MovingB",out mb);
                a.transform.localPosition=new Vector3(-.019f,0,0);b.transform.localPosition=new Vector3(.019f,.005f,-.005f);
                using var solver=new TissueContactSolver();solver.Initialize(new[]{a,b});
                float before=solver.Measure(true);
                for(int tick=0;tick<45;tick++)
                {
                    a.Step(1f/90,Vector3.zero);b.Step(1f/90,Vector3.zero);solver.Solve(true);
                }
                Assert(solver.AppliedConstraints>1,"a substep projects multiple real penetrating surface probes instead of one deepest point");
                Assert(solver.MaximumResidualPenetrationMeters<before*.8f,"sampled residual decreases despite intervening elastic return every90Hz substep");
                float settled=solver.MaximumResidualPenetrationMeters;
                Assert(a.Cage.BeginHandle(a.Cage.Rest[0]),"moving-contact fixture acquires actual cage handle");
                for(int tick=0;tick<30;tick++)
                {
                    var target=a.Cage.Rest[0]+Vector3.right*Mathf.Min(.006f,tick*.03f/90);
                    a.Step(1f/90,target);b.Step(1f/90,Vector3.zero);solver.Solve(true);
                    Positive(a.Cage);Positive(b.Cage);
                    Assert(solver.MaximumResidualPenetrationMeters<before,"3cm/s grasp does not outrun contact back to initial penetration");
                }
                Debug.Log($"SCALPAL_NATIVE_CONTACT_MOTION beforeMm={before*1000:F3} settledMm={settled*1000:F3} movingMm={solver.MaximumResidualPenetrationMeters*1000:F3} constraints={solver.AppliedConstraints} synthetic=true questTimingMeasured=false");
            }
            finally
            {foreach(var tissue in root.GetComponentsInChildren<DeformableTissue>(true))tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(root);if(ma)UnityEngine.Object.DestroyImmediate(ma);if(mb)UnityEngine.Object.DestroyImmediate(mb);}
        }

        static void ValidateTerminalCaps()
        {
            var root=new GameObject("SyntheticTerminalLoopContactProxy");var meshes=new List<Mesh>();var tissues=new List<DeformableTissue>();
            try
            {
                var closed=Body(root,"ClosedVessel",out var closedMesh);meshes.Add(closedMesh);tissues.Add(closed);
                var originalClosedVertices=closedMesh.vertices;var originalClosedTriangles=closedMesh.triangles;
                // Independently choose the two triangles on the known +Z cube terminal face.
                var retained=new List<int>();
                for(int triangle=0;triangle<originalClosedTriangles.Length;triangle+=3)
                {
                    var a=originalClosedVertices[originalClosedTriangles[triangle]];var b=originalClosedVertices[originalClosedTriangles[triangle+1]];var c=originalClosedVertices[originalClosedTriangles[triangle+2]];
                    if(Mathf.Abs(a.z-.02f)<1e-7f&&Mathf.Abs(b.z-.02f)<1e-7f&&Mathf.Abs(c.z-.02f)<1e-7f)continue;
                    retained.Add(originalClosedTriangles[triangle]);retained.Add(originalClosedTriangles[triangle+1]);retained.Add(originalClosedTriangles[triangle+2]);
                }
                closed.RestoreSource();closedMesh.triangles=retained.ToArray();Assert(closed.Initialize(TissuePreset.Bowel),"open terminal source initializes cage before proxy validation");
                var openVertices=closedMesh.vertices;var openTriangles=closedMesh.triangles;
                var vesselPart=closed.gameObject.AddComponent<Scalpal.Anatomy.AnatomyPart>();vesselPart.stableId="appendicular_artery";
                using var solver=new TissueContactSolver();solver.Initialize(new[]{closed});var report=solver.SurfaceReports[0];
                Assert(solver.SupportedBodies==1&&report.ArtificiallyCappedContactProxy&&report.OriginalOpenOrNonmanifoldEdges==4&&report.OpenOrNonmanifoldEdges==0,"simple planar terminal loop gets explicit closed contact proxy rather than altered source geometry");
                Assert(report.CapLoops==1&&report.AdditionalCapTriangles==4&&report.MaximumCapPlaneErrorMeters<1e-7f,"terminal fan reports bounded cap provenance and metric planarity");
                Assert(solver.TryCopyContactProxy("appendicular_artery",out var proxyVertices,out var proxyTriangles),"contact proxy geometry is independently inspectable");
                IndependentClosedProxy(proxyVertices,proxyTriangles,.04f*.04f*.04f);
                Assert(Equal(openTriangles,closedMesh.triangles)&&!AnyChanged(openVertices,closedMesh.vertices),"terminal cap leaves imported render/scoring source immutable");
                Assert(closed.GetComponent<MeshFilter>().sharedMesh.triangles.Length==openTriangles.Length,"contact cap never replaces existing display topology");

                // Four corners cease to be planar; moving every duplicate preserves true boundary adjacency.
                var bent=Body(root,"NonplanarVessel",out var bentMesh);meshes.Add(bentMesh);tissues.Add(bent);bent.RestoreSource();bentMesh.triangles=openTriangles;
                var bentVertices=bentMesh.vertices;
                for(int i=0;i<bentVertices.Length;i++)if(bentVertices[i].x>.019f&&bentVertices[i].y>.019f&&bentVertices[i].z>.019f)bentVertices[i].z+=.0004f;
                bentMesh.vertices=bentVertices;bentMesh.RecalculateBounds();Assert(bent.Initialize(TissuePreset.Bowel),"nonplanar contact fixture initializes from its actual source");var bentPart=bent.gameObject.AddComponent<Scalpal.Anatomy.AnatomyPart>();bentPart.stableId="appendicular_artery";
                solver.Initialize(new[]{bent});Assert(solver.SupportedBodies==0&&!solver.SurfaceReports[0].ArtificiallyCappedContactProxy&&solver.SurfaceReports[0].CapFailureReason=="Terminal loop exceeds metric plane tolerance","nonplanar vessel boundary exceeding50µm is not blindly capped and reports exact rejection");
                var nonmanifold=Body(root,"NonmanifoldVessel",out var nonmanifoldMesh);meshes.Add(nonmanifoldMesh);tissues.Add(nonmanifold);nonmanifold.RestoreSource();
                var duplicate=new List<int>(originalClosedTriangles);duplicate.AddRange(new[]{originalClosedTriangles[0],originalClosedTriangles[1],originalClosedTriangles[2]});nonmanifoldMesh.triangles=duplicate.ToArray();Assert(nonmanifold.Initialize(TissuePreset.Bowel),"nonmanifold source fixture still has finite cage bounds");
                var nonmanifoldPart=nonmanifold.gameObject.AddComponent<Scalpal.Anatomy.AnatomyPart>();nonmanifoldPart.stableId="appendicular_artery";
                solver.Initialize(new[]{nonmanifold});Assert(solver.SupportedBodies==0&&solver.SurfaceReports[0].OriginalNonmanifoldEdgeCount>0&&solver.SurfaceReports[0].FailureReason=="Nonmanifold surface","nonmanifold branching geometry cannot masquerade as terminal loops");
                var tooMany=Body(root,"TooManyVesselEnds",out var tooManyMesh);meshes.Add(tooManyMesh);tissues.Add(tooMany);tooMany.RestoreSource();
                var combinedVertices=new List<Vector3>();var combinedTriangles=new List<int>();
                for(int tube=0;tube<=TissueContactSolver.MaximumTerminalLoops;tube++)
                {
                    int offset=combinedVertices.Count;foreach(var vertex in openVertices)combinedVertices.Add(vertex+Vector3.right*(tube*.08f));foreach(int index in openTriangles)combinedTriangles.Add(index+offset);
                }
                tooManyMesh.Clear();tooManyMesh.vertices=combinedVertices.ToArray();tooManyMesh.triangles=combinedTriangles.ToArray();tooManyMesh.RecalculateBounds();Assert(tooMany.Initialize(TissuePreset.Bowel),"bounded multi-terminal source fixture initializes");var manyPart=tooMany.gameObject.AddComponent<Scalpal.Anatomy.AnatomyPart>();manyPart.stableId="appendicular_artery";
                solver.Initialize(new[]{tooMany});Assert(solver.SupportedBodies==0&&!solver.SurfaceReports[0].ArtificiallyCappedContactProxy,"terminal loop budget refuses entire proxy atomically");
            }
            finally
            {
                foreach(var tissue in tissues)tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(root);foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
        static void ValidateSmallGeometry()
        {
            var root=new GameObject("SyntheticSubMillimeterVesselContact");var meshes=new List<Mesh>();var tissues=new List<DeformableTissue>();
            try
            {
                // A 0.6 mm diameter, twelve-sided open tube models the actual vessel endpoint scale.
                const int sides=12;const float radius=.0003f,length=.004f;
                var vertices=new Vector3[sides*2];var triangles=new List<int>();
                for(int ring=0;ring<2;ring++)for(int i=0;i<sides;i++)
                {float angle=i*2*Mathf.PI/sides;vertices[ring*sides+i]=new Vector3(radius*Mathf.Cos(angle),radius*Mathf.Sin(angle),(ring==0?-1:1)*length*.5f);}
                for(int i=0;i<sides;i++)
                {int next=(i+1)%sides;triangles.AddRange(new[]{i,next,next+sides,i,next+sides,i+sides});}
                var tube=new GameObject("SmallOpenVessel");tube.transform.SetParent(root.transform,false);
                var mesh=new Mesh {name="SyntheticSubMillimeterTerminalTube",vertices=vertices,triangles=triangles.ToArray()};mesh.RecalculateBounds();meshes.Add(mesh);
                tube.AddComponent<MeshFilter>().sharedMesh=mesh;tube.AddComponent<MeshCollider>().sharedMesh=mesh;
                var part=tube.AddComponent<Scalpal.Anatomy.AnatomyPart>();part.stableId="appendicular_artery";
                var tissue=tube.AddComponent<DeformableTissue>();tissues.Add(tissue);Assert(tissue.Initialize(TissuePreset.Artery),"sub-millimeter tube has valid source-meter cage");
                using var solver=new TissueContactSolver();solver.Initialize(new[]{tissue});var report=solver.SurfaceReports[0];
                Assert(report.Supported&&report.ArtificiallyCappedContactProxy&&report.CapLoops==2&&report.AdditionalCapTriangles==24,"tiny planar vessel endpoints retain nonzero area normals and close both twelve-edge loops");
                Assert(report.OriginalBoundaryEdgeCount==24&&report.OriginalNonmanifoldEdgeCount==0&&report.OriginalInconsistentWindingEdgeCount==0&&report.BoundaryEdgeCount==0&&report.FailureReason==""&&report.CapFailureReason=="","tiny terminal proxy reports original and postclosure topology separately");
                Assert(solver.TryCopyContactProxy("appendicular_artery",out var proxyVertices,out var proxyTriangles),"tiny generated vessel proxy remains inspectable");
                IndependentClosedProxy(proxyVertices,proxyTriangles,sides*.5f*radius*radius*Mathf.Sin(2*Mathf.PI/sides)*length);
                Assert(!AnyChanged(vertices,mesh.vertices)&&Equal(triangles.ToArray(),mesh.triangles),"tiny terminal closure never edits original vessel source");

                var a=Body(root,"SmallA",out var sourceA);var b=Body(root,"SmallB",out var sourceB);meshes.AddRange(new[]{sourceA,sourceB});tissues.AddRange(new[]{a,b});
                foreach(var body in new[]{a,b})
                {
                    body.RestoreSource();var source=body.GetComponent<MeshFilter>().sharedMesh;var small=source.vertices;
                    for(int i=0;i<small.Length;i++)small[i]*=.05f;source.vertices=small;source.RecalculateBounds();
                    Assert(body.Initialize(TissuePreset.Bowel),"two-millimeter contact body binds actual small geometry");
                    var indices=source.triangles;
                    for(int i=0;i<indices.Length;i+=3)Assert(Vector3.Cross(small[indices[i+1]]-small[indices[i]],small[indices[i+2]]-small[indices[i]]).magnitude<1e-5f,"contact regression triangle area is below Unity normalized epsilon");
                }
                a.transform.localPosition=new Vector3(-.00095f,0,0);b.transform.localPosition=new Vector3(.00095f,.0002f,-.0002f);
                solver.Initialize(new[]{a,b});Assert(solver.SupportedBodies==2,"small closed geometry is supported independently of cap repair");
                Assert(solver.Measure(true)>0,"tiny world triangle normals expose actual sampled overlap instead of silently skipping all triangles");
                var beforeA=Copy(a.Cage);var beforeB=Copy(b.Cage);solver.Solve(true);
                Assert(solver.AppliedPairs<=1&&solver.RejectedPairs<=1&&solver.AppliedPairs+solver.RejectedPairs>=1,"tiny pair evaluates all samples and reports unsafe pinned/inverting constraints even when other samples apply");
                if(solver.AppliedPairs==1)Assert(Motion(beforeA,a.Cage)>0&&Motion(beforeB,b.Cage)>0,"accepted tiny contact corrects both bodies");
                else Assert(Motion(beforeA,a.Cage)==0&&Motion(beforeB,b.Cage)==0,"unsafe tiny contact rolls back both bodies atomically");
                Positive(a.Cage);Positive(b.Cage);
            }
            finally
            {foreach(var tissue in tissues)tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(root);foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);}
        }

        static void ValidateAuthoredAttachments()
        {
            var root=new GameObject("SyntheticAuthoredTissueAttachments");Mesh sourceA=null,sourceB=null;
            var tissues=new List<DeformableTissue>();
            try
            {
                var a=Body(root,"AttachedA",out sourceA);var b=Body(root,"AttachedB",out sourceB);tissues.AddRange(new[]{a,b});
                a.transform.localPosition=new Vector3(-.019f,0,0);b.transform.localPosition=new Vector3(.019f,.005f,-.005f);
                var beforeA=Copy(a.Cage);var beforeB=Copy(b.Cage);
                // Baseline acquisition deliberately ignores a hidden presentation and enabled colliders.
                a.gameObject.SetActive(false);b.GetComponent<MeshCollider>().enabled=false;
                using var solver=new TissueContactSolver();solver.Initialize(new[]{a,b},true);
                Assert(solver.PreservesAuthoredRestOverlap&&solver.MaximumAuthoredOverlapMeters>0,"opt-in reports real authored overlap even while presentation is hidden");
                a.gameObject.SetActive(true);b.GetComponent<MeshCollider>().enabled=true;
                solver.Solve(true);
                Assert(solver.ActiveBodies==2&&solver.AppliedPairs==0&&solver.RejectedPairs==0&&solver.MaximumExcessResidualMeters==0,"authored embedded surfaces acquire no rest force");
                Assert(Motion(beforeA,a.Cage)==0&&Motion(beforeB,b.Cage)==0,"preserving attachments leaves both rest cages unchanged");
                float originalOverlap=solver.MaximumAuthoredOverlapMeters;
                root.transform.SetPositionAndRotation(new Vector3(.2f,.1f,-.15f),Quaternion.Euler(13,37,-9));root.transform.localScale=Vector3.one*.5f;
                solver.Solve(true);
                Assert(solver.AppliedPairs==0&&solver.RejectedPairs==0&&solver.MaximumExcessResidualMeters==0&&Motion(beforeA,a.Cage)==0&&Motion(beforeB,b.Cage)==0,"rigid and uniform smaller fit transforms preserve source-frame attachment allowance without padding artifacts");
                Assert(Mathf.Abs(solver.MaximumAuthoredOverlapMeters-originalOverlap*.5f)<1e-6f,"declared authored overlap scales with current world fit");
                root.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);root.transform.localScale=Vector3.one;
                b.transform.localPosition+=Vector3.left*.001f;
                Assert(solver.Measure(true)>0,"additional penetration is detected beyond the fixed per-probe authored allowance");
                solver.Solve(true);
                Assert(solver.AppliedPairs==1&&Motion(beforeA,a.Cage)>0&&Motion(beforeB,b.Cage)>0,"authored attachment allowance does not blanket-exempt pair from later two-way contact");
                Positive(a.Cage);Positive(b.Cage);
                a.ResetTissue();b.ResetTissue();b.transform.localPosition=new Vector3(.019f,.005f,-.005f);
                solver.Solve(true);Assert(solver.AppliedPairs==0&&solver.MaximumExcessResidualMeters==0,"reset restores original fixed attachment baseline rather than remembering deformed overlap");
                // Acquisition must use original source geometry even if a caller already pulled a cage.
                Assert(a.ApplyContact(a.Cage.Rest[0],Vector3.left*.0001f),"authored baseline independence fixture can deform a free node");
                solver.Initialize(new[]{a,b},true);a.ResetTissue();solver.Solve(true);
                Assert(solver.AppliedPairs==0&&solver.MaximumExcessResidualMeters==0,"new baseline ignores previous deformation and binds undeformed authored source");
                var originalA=sourceA.vertices;var originalB=sourceB.vertices;solver.Solve(false);
                Assert(solver.AppliedPairs==0&&solver.MaximumAuthoredOverlapMeters==0&&solver.MaximumExcessResidualMeters==0,"registration loss clears current attachment diagnostics and prevents corrections");
                Assert(!AnyChanged(originalA,sourceA.vertices)&&!AnyChanged(originalB,sourceB.vertices),"attachment allowances do not modify imported source geometry");
            }
            finally
            {foreach(var tissue in tissues)tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(root);if(sourceA)UnityEngine.Object.DestroyImmediate(sourceA);if(sourceB)UnityEngine.Object.DestroyImmediate(sourceB);}
        }

        static void IndependentClosedProxy(Vector3[] vertices,int[] triangles,float expectedVolume)
        {
            var directed=new Dictionary<string,int>();var incidence=new Dictionary<string,int>();float volume=0;
            string Key(Vector3 v)=>Mathf.RoundToInt(v.x*1e6f)+":"+Mathf.RoundToInt(v.y*1e6f)+":"+Mathf.RoundToInt(v.z*1e6f);
            for(int triangle=0;triangle<triangles.Length;triangle+=3)
            {
                var a=vertices[triangles[triangle]];var b=vertices[triangles[triangle+1]];var c=vertices[triangles[triangle+2]];volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6;
                for(int edge=0;edge<3;edge++)
                {
                    string first=Key(vertices[triangles[triangle+edge]]),second=Key(vertices[triangles[triangle+(edge+1)%3]]);
                    bool ascending=string.CompareOrdinal(first,second)<0;string key=ascending?first+"/"+second:second+"/"+first;
                    incidence.TryGetValue(key,out int count);incidence[key]=count+1;directed.TryGetValue(key,out int direction);directed[key]=direction+(ascending?1:-1);
                }
            }
            foreach(var edge in incidence)Assert(edge.Value==2&&directed[edge.Key]==0,"independent edge audit proves cap winding cancels source boundary direction");
            Assert(Mathf.Abs(volume-expectedVolume)<expectedVolume*.001f,"terminal contact cap encloses known physical volume with outward winding");
        }

        static void ValidateBvhQueries(TissueContactSolver solver,List<DeformableTissue> tissues)
        {
            foreach(var tissue in tissues)
            {
                string id=tissue.GetComponent<Scalpal.Anatomy.AnatomyPart>().stableId;
                Assert(solver.TryCopyContactProxy(id,out var source,out var triangles),"BVH oracle reads real capped contact proxy: "+id);
                var world=new Vector3[source.Length];var points=new Vector3[128];
                using var acceleration=new TissueSurfaceBvh(source,triangles,points.Length);
                for(int pose=0;pose<3;pose++)
                {
                    int movable=0;while(tissue.Cage.IsPinned(movable))movable++;
                    if(pose==1)Assert(tissue.ApplyContact(tissue.Cage.Rest[movable],new Vector3(.0002f,-.0001f,-.0002f)),"BVH fixture genuinely deforms current cage");
                    var rotation=pose==2?Quaternion.Euler(23,41,-17):Quaternion.identity;
                    for(int i=0;i<source.Length;i++)world[i]=rotation*tissue.ToMeters(tissue.DeformSurfacePoint(source[i]))*1.3f+new Vector3(.2f,-.1f,.3f);
                    for(int i=0;i<points.Length;i++)
                    {
                        int at=i*source.Length/points.Length;
                        points[i]=world[at]+rotation*new Vector3((i%3-1)*.0011f,(i%5-2)*.0007f,(i%7-3)*.0004f);
                    }
                    acceleration.Refit(world);acceleration.Query(points,points.Length);
                    int full=points.Length*triangles.Length/3;
                    Assert(acceleration.LastTriangleTests<full/3,"real-source BVH prunes at least two thirds of full triangle scans: "+id);
                    for(int query=0;query<points.Length;query++)
                    {
                        float nearest=float.PositiveInfinity,signed=0;
                        for(int t=0;t<triangles.Length;t+=3)
                        {
                            var a=world[triangles[t]];var b=world[triangles[t+1]];var c=world[triangles[t+2]];
                            var n=Vector3.Cross(b-a,c-a);float length=n.magnitude;if(length*length<=1e-20f)continue;n/=length;
                            var closest=TissueVolume.ClosestTriangle(points[query],a,b,c);float d=(points[query]-closest).sqrMagnitude,dot=Vector3.Dot(points[query]-closest,n);
                            if(d>nearest+1e-12f||(Mathf.Abs(d-nearest)<1e-12f&&dot<signed))continue;
                            nearest=d;signed=dot;
                        }
                        var hit=acceleration.Result(query);float expected=(signed<0?1:-1)*Mathf.Sqrt(nearest);
                        Assert(hit.triangle>=0&&Mathf.Abs(hit.penetration-expected)<.000003f,"Burst BVH agrees with independent brute-force signed closest-distance oracle in deformed/transformed source: "+id);
                    }
                }
                var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int repeat=0;repeat<64;repeat++)acceleration.Query(points,points.Length);
                watch.Stop();
                Debug.Log($"SCALPAL_NATIVE_CONTACT_BVH_BENCH id={id} triangles={triangles.Length/3} probes={points.Length} closestQueries={acceleration.LastTriangleTests} bruteForceQueries={points.Length*triangles.Length/3} editorMeanQueryMs={watch.Elapsed.TotalMilliseconds/64:F4} questTimingMeasured=false");
                tissue.ResetTissue();
            }
        }

        static void AuditNativeMeshes()
        {
            var session=UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Assert(session&&session.anatomy,"native scene has actual practice anatomy to audit contact");
            var root=new GameObject("NativeContactSourceAudit");
            var tissues=new List<DeformableTissue>();var originals=new List<Mesh>();var saved=new List<Vector3[]>();
            try
            {
                var ids=new[]{"appendix","mesoappendix","appendicular_artery"};
                var presets=new[]{TissuePreset.Bowel,TissuePreset.Mesentery,TissuePreset.Artery};
                for(int i=0;i<ids.Length;i++)
                {
                    var sourcePart=session.anatomy.GetComponentsInChildren<Scalpal.Anatomy.AnatomyPart>(true).SingleOrDefault(part=>part.stableId==ids[i]);
                    Assert(sourcePart,"actual native contact source exists: "+ids[i]);
                    var source=sourcePart.GetComponent<MeshFilter>().sharedMesh;
                    Assert(source&&source.isReadable&&AssetDatabase.Contains(source),"contact audit uses actual imported mesh: "+ids[i]);
                    originals.Add(source);saved.Add(source.vertices);
                    var clone=UnityEngine.Object.Instantiate(sourcePart.gameObject,root.transform);
                    clone.transform.SetLocalPositionAndRotation(session.anatomy.transform.InverseTransformPoint(sourcePart.transform.position),Quaternion.Inverse(session.anatomy.transform.rotation)*sourcePart.transform.rotation);
                    clone.transform.localScale=sourcePart.transform.lossyScale/session.anatomy.transform.lossyScale.x;
                    clone.GetComponent<Scalpal.Anatomy.AnatomyPart>().SetVisible(true);
                    var body=clone.GetComponent<DeformableTissue>()??clone.AddComponent<DeformableTissue>();
                    var factor=sourcePart.transform.lossyScale/session.anatomy.transform.lossyScale.x;
                    Assert(factor.x>0&&Mathf.Abs(factor.x-factor.y)<factor.x*.001f&&Mathf.Abs(factor.x-factor.z)<factor.x*.001f,"actual native FBX source units are uniform: "+ids[i]);
                    var posterior=clone.transform.InverseTransformDirection(root.transform.TransformDirection(Vector3.forward));
                    Assert(body.Initialize(presets[i],factor.x,posterior),"actual source binds metric deformation cage and transformed posterior attachments: "+ids[i]);tissues.Add(body);
                }
                using var solver=new TissueContactSolver();solver.Initialize(tissues);
                Assert(solver.SurfaceReports.Count==3,"every native target receives explicit contact support report");
                int supported=0;
                foreach(var report in solver.SurfaceReports)
                {
                    Assert(report.Vertices>0&&report.Triangles>0&&report.FiniteGeometry,"actual native contact report has finite imported geometry: "+report.BodyId);
                    if(report.Supported)
                    {
                        supported++;
                        Assert(report.OpenOrNonmanifoldEdges==0&&report.DegenerateTriangles==0&&report.SignedReferenceVolumeM3>0,"supported actual body has closed outward geometry: "+report.BodyId);
                        Assert(report.ProbeCount>0&&report.ProbeCount<=TissueContactSolver.MaximumProbes&&report.SamplingGapSourceMeters>0&&!float.IsNaN(report.SamplingGapSourceMeters),"actual body stays within sample budget with declared source-meter bound: "+report.BodyId);
                    }
                    else Assert(report.OpenOrNonmanifoldEdges>0||report.DegenerateTriangles>0||report.SignedReferenceVolumeM3<=1e-12f||report.InconsistentWindingEdgeCount>0,"unsupported actual body identifies geometry failure instead of silently pretending to collide: "+report.BodyId);
                    Debug.Log($"SCALPAL_NATIVE_CONTACT_SOURCE id={report.BodyId} supported={report.Supported} vertices={report.Vertices} triangles={report.Triangles} originalOpenOrNonmanifoldEdges={report.OriginalOpenOrNonmanifoldEdges} originalBoundaryEdges={report.OriginalBoundaryEdgeCount} originalNonmanifoldEdges={report.OriginalNonmanifoldEdgeCount} originalInconsistentWindingEdges={report.OriginalInconsistentWindingEdgeCount} failureReason={report.FailureReason} capFailureReason={report.CapFailureReason} artificiallyCappedContactProxy={report.ArtificiallyCappedContactProxy} capLoops={report.CapLoops} additionalCapTriangles={report.AdditionalCapTriangles} capPlaneErrorMeters={report.MaximumCapPlaneErrorMeters:F8} postClosureOpenOrNonmanifoldEdges={report.OpenOrNonmanifoldEdges} degenerateTriangles={report.DegenerateTriangles} sourceUnitScale={report.SourceUnitScale:G6} rawBounds={report.RawBoundsSize.ToString("F6")} metricBounds={(report.RawBoundsSize*report.SourceUnitScale).ToString("F6")} signedVolumeM3={report.SignedReferenceVolumeM3:G6} probes={report.ProbeCount} samplingGapSourceMeters={report.SamplingGapSourceMeters:F6}");
                }
                Assert(solver.SupportedBodies==supported,"native solver count agrees with audited supported sources");
                Assert(supported==3,"all three native tissue surfaces must support declared contact after FBX unit normalization; true geometry holes require a fix");
                ValidateBvhQueries(solver,tissues);
                var artery=solver.SurfaceReports.Single(report=>report.BodyId=="appendicular_artery");
                Assert(artery.ArtificiallyCappedContactProxy&&artery.OriginalOpenOrNonmanifoldEdges>0&&artery.CapLoops>0&&artery.CapLoops<=TissueContactSolver.MaximumTerminalLoops&&artery.AdditionalCapTriangles>0,
                    "native open vessel endpoints receive explicitly disclosed bounded contact caps");
                Assert(artery.MaximumCapPlaneErrorMeters<=TissueContactSolver.MaximumCapPlaneErrorMeters&&artery.OpenOrNonmanifoldEdges==0,
                    "native vessel proxy closes only supported planar loops without repairing true branching/nonmanifold holes");
                var restSnapshots=new List<Vector3[]>();foreach(var tissue in tissues)restSnapshots.Add(Copy(tissue.Cage));
                float initialPenetration=solver.Measure(true);
                Assert(solver.ActiveBodies==3&&solver.AppliedPairs==0,"actual resting contact measurement uses all three common-frame bodies without modifying attachments");
                for(int i=0;i<tissues.Count;i++)Assert(Motion(restSnapshots[i],tissues[i].Cage)==0,"rest-overlap inspection is read-only before applying explicit attachment policy");
                solver.Initialize(tissues,true);solver.Solve(true);
                Assert(solver.PreservesAuthoredRestOverlap&&solver.ActiveBodies==3&&solver.AppliedPairs==0&&solver.RejectedPairs==0&&solver.MaximumExcessResidualMeters==0,"actual native authored attachments receive no baseline separation force");
                Assert(Mathf.Abs(solver.MaximumAuthoredOverlapMeters-initialPenetration)<1e-6f,"actual native overlap is disclosed separately from zero excess residual");
                for(int i=0;i<tissues.Count;i++)Assert(Motion(restSnapshots[i],tissues[i].Cage)==0,"actual source rest cages remain unchanged under authored attachment allowance");
                Debug.Log($"SCALPAL_NATIVE_CONTACT_REST_AUDIT activeBodies={solver.ActiveBodies} initialSampledPenetrationMeters={initialPenetration:F6} authoredOverlapMeters={solver.MaximumAuthoredOverlapMeters:F6} preservesAuthoredRestOverlap={solver.PreservesAuthoredRestOverlap} appliedPairs={solver.AppliedPairs} rejectedPairs={solver.RejectedPairs} excessResidualMeters={solver.MaximumExcessResidualMeters:F6} conservativeSamplingGapMeters={solver.MaximumSamplingGapMeters:F6} attachmentPolicy=FixedPerProbeSourceFrameAllowance");
                for(int i=0;i<originals.Count;i++)
                {
                    var current=originals[i].vertices;
                    for(int vertex=0;vertex<current.Length;vertex++)Assert(current[vertex]==saved[i][vertex],"native contact analysis leaves imported geometry unchanged");
                }
            }
            finally
            {
                foreach(var tissue in tissues)tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void ValidateImportUnits(GameObject root)
        {
            var fixture=new GameObject("SyntheticFbxUnitNormalization");fixture.transform.SetParent(root.transform,false);
            var meshes=new List<Mesh>();var tissues=new List<DeformableTissue>();
            try
            {
                var meterA=Body(fixture,"MeterA",out var sourceA);var meterB=Body(fixture,"MeterB",out var sourceB);
                var rawA=Body(fixture,"Fbx100A",out var sourceRawA,100);var rawB=Body(fixture,"Fbx100B",out var sourceRawB,100);
                meshes.AddRange(new[]{sourceA,sourceB,sourceRawA,sourceRawB});tissues.AddRange(new[]{meterA,meterB,rawA,rawB});
                meterA.transform.localPosition=rawA.transform.localPosition=new Vector3(-.019f,0,0);
                meterB.transform.localPosition=rawB.transform.localPosition=new Vector3(.019f,.005f,-.005f);
                Assert(rawA.SourceUnitScale==100&&rawB.SourceUnitScale==100,"FBX import factor is explicit rather than interpreted as display meters");
                for(int i=0;i<8;i++)Assert(Vector3.Distance(meterA.Cage.Rest[i],rawA.Cage.Rest[i])<1e-7f,"100x FBX import produces same physical meter cage");
                Assert(Vector3.Distance(rawA.FromMeters(rawA.ToMeters(sourceRawA.vertices[0])),sourceRawA.vertices[0])<1e-9f,"raw-to-meter coordinate conversion round trips");
                using var meterSolver=new TissueContactSolver();meterSolver.Initialize(new[]{meterA,meterB});
                using var rawSolver=new TissueContactSolver();rawSolver.Initialize(new[]{rawA,rawB});
                Assert(rawSolver.SupportedBodies==2,"unit conversion preserves manifold support for 100x import geometry");
                Assert(Mathf.Abs(meterSolver.SurfaceReports[0].SignedReferenceVolumeM3-rawSolver.SurfaceReports[0].SignedReferenceVolumeM3)<1e-9f,"signed volume uses cubic meters irrespective of import units");
                Assert(Mathf.Abs(meterSolver.SurfaceReports[0].SamplingGapSourceMeters-rawSolver.SurfaceReports[0].SamplingGapSourceMeters)<1e-6f,"sampling gap uses source meters rather than raw FBX units");
                meterSolver.Solve(true);rawSolver.Solve(true);
                Assert(meterSolver.AppliedPairs==1&&rawSolver.AppliedPairs==1,"world-space contact responds equally for meter and 100x imported mesh representations");
                for(int i=0;i<8;i++)Assert(Vector3.Distance(meterA.Cage.Positions[i],rawA.Cage.Positions[i])<1e-5f&&Vector3.Distance(meterB.Cage.Positions[i],rawB.Cage.Positions[i])<1e-5f,"physical contact correction is independent of FBX import units");
                rawA.CommitSurface();meterA.CommitSurface();
                var meterVertices=meterA.GetComponent<MeshFilter>().sharedMesh.vertices;var rawVertices=rawA.GetComponent<MeshFilter>().sharedMesh.vertices;
                for(int i=0;i<meterVertices.Length;i++)Assert(Vector3.Distance(meterA.transform.TransformPoint(meterVertices[i]),rawA.transform.TransformPoint(rawVertices[i]))<1e-5f,"normalized correction reaches same world render/contact surface");
                Assert(!rawA.Initialize(TissuePreset.Bowel,1),"already initialized cage cannot silently switch conversion factor");
                var invalid=new GameObject("InvalidContactUnits");invalid.transform.SetParent(fixture.transform,false);
                invalid.AddComponent<MeshFilter>().sharedMesh=sourceA;invalid.AddComponent<MeshCollider>().sharedMesh=sourceA;var body=invalid.AddComponent<DeformableTissue>();
                Assert(!body.Initialize(TissuePreset.Bowel,0)&&!body.Initialize(TissuePreset.Bowel,-1)&&!body.Initialize(TissuePreset.Bowel,float.NaN)&&!body.Initialize(TissuePreset.Bowel,float.PositiveInfinity),"nonpositive or nonfinite source conversion fails before creating a cage");
            }
            finally
            {
                foreach(var tissue in tissues)tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(fixture);
                foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        static DeformableTissue Body(GameObject root,string name,out Mesh source,float unitFactor=1)
        {
            var go=new GameObject(name);go.transform.SetParent(root.transform,false);
            var material=new VolumeMaterial {id="synthetic_contact_fixture",youngPascals=12000,poissonRatio=.3f,densityKgPerCubicMeter=1000,color=Color.red};
            var volume=TissueVolumeFactory.Box(new Bounds(Vector3.zero,Vector3.one*.04f),1,1,1,material,false);
            source=new Mesh {name="SyntheticClosedContactSource"};volume.WriteSurface(source);
            if(unitFactor!=1){var vertices=source.vertices;for(int i=0;i<vertices.Length;i++)vertices[i]/=unitFactor;source.vertices=vertices;source.RecalculateBounds();go.transform.localScale=Vector3.one*unitFactor;}
            go.AddComponent<MeshFilter>().sharedMesh=source;go.AddComponent<MeshCollider>().sharedMesh=source;
            var body=go.AddComponent<DeformableTissue>();Assert(body.Initialize(TissuePreset.Bowel,unitFactor),"synthetic contact cage initializes from readable geometry with explicit units");return body;
        }
        static bool AnyChanged(Vector3[] a,Vector3[] b){for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return true;return false;}
        static bool Equal(int[] a,int[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
    }
}
