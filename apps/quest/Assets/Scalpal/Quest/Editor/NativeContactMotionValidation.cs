using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Actual imported surfaces and production cage/contact code, with synthetic tool
    // targets. This is not a physical headset test or a continuous-collision proof.
    public static class NativeContactMotionValidation
    {
        static int checks;
        static readonly string[] Ids={"appendix","mesoappendix","appendicular_artery"};
        static readonly TissuePreset[] Presets={TissuePreset.Bowel,TissuePreset.Mesentery,TissuePreset.Artery};
        static readonly int[,] Cells={{0,1,2,4},{1,2,3,7},{1,4,5,7},{2,4,6,7},{1,2,4,7}};
        static void Assert(bool condition,string message)
        {checks++;if(!condition)throw new InvalidOperationException("Imported contact motion: "+message);}

        [MenuItem("Scalpal/Quest/Validate Imported Contact Motion")]
        public static void Run()
        {
            NativeSessionBuild.Validate();checks=0;
            var session=UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Assert(session&&session.anatomy,"committed native scene provides production anatomy");
            ValidateFrozenPlaneProjection();
            for(int body=0;body<Ids.Length;body++)
                for(int path=0;path<2;path++)RunMotion(session,body,path);
            Debug.Log($"SCALPAL_NATIVE_CONTACT_MOTION_VALIDATION_OK checks={checks} actualImportedBodies=3 syntheticGraspPaths=6 continuousCollisionProven=false questTimingMeasured=false");
        }

        // Isolate the actual production projection from BVH normal refresh: many
        // probes can describe the same material plane in one frozen Gather batch.
        // Reflection constructs private records, not an alternate contact algorithm.
        // Independent accepted-point geometry detects stale-depth overprojection.
        static void ValidateFrozenPlaneProjection()
        {
            var root=new GameObject("FrozenMaterialPlaneRegression");
            var meshes=new List<Mesh>();var tissues=new List<DeformableTissue>();
            try
            {
                for(int i=0;i<2;i++)
                {
                    var child=new GameObject("PlaneBody"+i);child.transform.SetParent(root.transform,false);
                    var material=new VolumeMaterial{id="contact_plane_fixture",youngPascals=12000,poissonRatio=.3f,densityKgPerCubicMeter=1000,color=Color.red};
                    var volume=TissueVolumeFactory.Box(new Bounds(Vector3.zero,Vector3.one*.04f),1,1,1,material,false);
                    var mesh=new Mesh{name="ClosedFrozenPlaneSource"};volume.WriteSurface(mesh);meshes.Add(mesh);
                    child.AddComponent<MeshFilter>().sharedMesh=mesh;child.AddComponent<MeshCollider>().sharedMesh=mesh;
                    var tissue=child.AddComponent<DeformableTissue>();
                    Assert(tissue.Initialize(TissuePreset.Bowel),"frozen-plane regression initializes real production cages");tissues.Add(tissue);
                }
                using var solver=new TissueContactSolver();
                var bodyType=typeof(TissueContactSolver).GetNestedType("Body",BindingFlags.NonPublic);
                var contactType=typeof(TissueContactSolver).GetNestedType("Contact",BindingFlags.NonPublic);
                var method=typeof(TissueContactSolver).GetMethod("TryProjectContact",BindingFlags.Instance|BindingFlags.NonPublic);
                Assert(bodyType!=null&&contactType!=null&&method!=null,"fixture targets the production projection used by Solve");
                void Set(object instance,string field,object value)=>instance.GetType().GetField(field,BindingFlags.Instance|BindingFlags.Public).SetValue(instance,value);
                var a=Activator.CreateInstance(bodyType,true);var b=Activator.CreateInstance(bodyType,true);
                Set(a,"tissue",tissues[0]);Set(b,"tissue",tissues[1]);Set(a,"scale",1f);Set(b,"scale",1f);
                var plane=Activator.CreateInstance(contactType);
                var restA=tissues[0].Cage.Rest[0];var restB=tissues[1].Cage.Rest[0];
                Set(plane,"a",a);Set(plane,"b",b);Set(plane,"restA",restA);Set(plane,"restB",restB);Set(plane,"normal",Vector3.right);
                Vector3 Point(DeformableTissue tissue,Vector3 rest)=>tissue.transform.TransformPoint(tissue.DeformSurfacePoint(rest));
                float initialGap=Vector3.Dot(Point(tissues[0],restA)-Point(tissues[1],restB),Vector3.right);
                const float requiredClosure=.002f;
                Set(plane,"depth",requiredClosure);Set(plane,"desiredSeparation",initialGap+requiredClosure);
                for(int repeat=0;repeat<16;repeat++)
                {
                    float beforeClosure=Vector3.Dot(Point(tissues[0],restA)-Point(tissues[1],restB),Vector3.right)-initialGap;
                    var before=tissues.Select(tissue=>(Vector3[])tissue.Cage.Positions.Clone()).ToArray();
                    var args=new object[]{plane,false};method.Invoke(solver,args);
                    // The cage rejects requested corrections below 0.1 micrometers;
                    // each body receives one quarter of the remaining plane gap.
                    // Do not confuse that numerical floor with unsafe cage geometry.
                    if((bool)args[1])
                    {
                        Assert(requiredClosure-beforeClosure<=.0000005f,"only submicrometer remainder may reach the cage correction floor (sample "+repeat+")");
                        for(int body=0;body<2;body++)for(int node=0;node<8;node++)
                            Assert(tissues[body].Cage.Positions[node]==before[body][node],"correction-floor rejection remains atomic");
                    }
                    float closure=Vector3.Dot(Point(tissues[0],restA)-Point(tissues[1],restB),Vector3.right)-initialGap;
                    Assert(closure<=requiredClosure+.000001f,"repeated frozen samples cannot push accepted material points beyond their contact-plane goal");
                    PositiveAndPinned(tissues[0].Cage);PositiveAndPinned(tissues[1].Cage);
                }
                float achieved=Vector3.Dot(Point(tissues[0],restA)-Point(tissues[1],restB),Vector3.right)-initialGap;
                Assert(achieved>=requiredClosure*.99f,"projection must actually approach the goal; skipping all contact cannot pass");
                var accepted=tissues.Select(tissue=>(Vector3[])tissue.Cage.Positions.Clone()).ToArray();
                Set(plane,"desiredSeparation",initialGap+achieved-.0001f);
                var satisfied=new object[]{plane,false};
                Assert(!(bool)method.Invoke(solver,satisfied)&&!(bool)satisfied[1],"a satisfied plane is skipped, not projected or reported unsafe");
                for(int body=0;body<2;body++)for(int node=0;node<8;node++)
                    Assert(tissues[body].Cage.Positions[node]==accepted[body][node],"satisfied contact cannot introduce spurious material motion");
                Debug.Log($"SCALPAL_FROZEN_CONTACT_PLANE requestedClosureMm={requiredClosure*1000:F3} acceptedClosureMm={achieved*1000:F6} repeatedSamples=16 synthetic=true");
            }
            finally
            {
                foreach(var tissue in tissues)tissue.RestoreSource();UnityEngine.Object.DestroyImmediate(root);
                foreach(var mesh in meshes)if(mesh)UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        sealed class Fixture : IDisposable
        {
            public readonly GameObject root;
            public readonly List<DeformableTissue> bodies=new List<DeformableTissue>();
            public readonly List<Vector3[]> originalVertices=new List<Vector3[]>();
            public readonly List<int[]> originalTriangles=new List<int[]>();
            public readonly TissueContactSolver contact=new TissueContactSolver();
            public Fixture(NativeCaseSession session)
            {
                root=new GameObject("ImportedMovingGraspContactFixture");
                // Every trajectory uses a nonidentity common frame. Import scaling,
                // local FBX rotations and anatomy-root posterior attachments are real.
                root.transform.SetPositionAndRotation(new Vector3(1.7f,.3f,-.8f),Quaternion.Euler(17,39,-11));
                root.transform.localScale=Vector3.one*1.3f;
                try
                {
                    for(int i=0;i<Ids.Length;i++)
                    {
                        var source=session.anatomy.GetComponentsInChildren<AnatomyPart>(true).SingleOrDefault(part=>part.stableId==Ids[i]);
                        Assert(source,"actual native target exists: "+Ids[i]);
                        var mesh=source.GetComponent<MeshFilter>().sharedMesh;
                        Assert(mesh&&mesh.isReadable&&AssetDatabase.Contains(mesh),"fixture is an imported asset, not a substitute cube: "+Ids[i]);
                        originalVertices.Add(mesh.vertices);originalTriangles.Add(mesh.triangles);
                        var clone=UnityEngine.Object.Instantiate(source.gameObject,root.transform);
                        clone.transform.SetLocalPositionAndRotation(session.anatomy.transform.InverseTransformPoint(source.transform.position),Quaternion.Inverse(session.anatomy.transform.rotation)*source.transform.rotation);
                        var scale=source.transform.lossyScale/session.anatomy.transform.lossyScale.x;
                        Assert(scale.x>0&&Mathf.Abs(scale.x-scale.y)<scale.x*.001f&&Mathf.Abs(scale.x-scale.z)<scale.x*.001f,"import scale remains uniform: "+Ids[i]);
                        clone.transform.localScale=scale;clone.GetComponent<AnatomyPart>().SetVisible(true);
                        if(!clone.TryGetComponent<DeformableTissue>(out var tissue))tissue=clone.AddComponent<DeformableTissue>();
                        var posterior=clone.transform.InverseTransformDirection(root.transform.TransformDirection(Vector3.forward));
                        Assert(tissue.Initialize(Presets[i],scale.x,posterior),"actual source binds meter cage/posterior axis: "+Ids[i]);
                        bodies.Add(tissue);
                    }
                    contact.Initialize(bodies,true);
                    Assert(contact.SupportedBodies==3&&contact.PreservesAuthoredRestOverlap,"all real bodies bind the runtime attachment policy");
                    contact.Solve(true);
                    Assert(contact.ActiveBodies==3&&contact.AppliedConstraints==0&&contact.MaximumExcessResidualMeters==0,"authored rest overlap is preserved before pulling");
                }
                catch{Dispose();throw;}
            }
            public void Dispose()
            {
                contact.Dispose();foreach(var tissue in bodies)if(tissue)tissue.RestoreSource();
                if(root)UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void RunMotion(NativeCaseSession session,int index,int path)
        {
            using var fixture=new Fixture(session);
            var moving=fixture.bodies[index];var cage=moving.Cage;
            int node=Enumerable.Range(0,8).First(candidate=>!cage.IsPinned(candidate));
            Assert(cage.BeginHandle(cage.Rest[node])&&cage.Handle==node,"grasp binds an actual movable cage corner: "+Ids[index]);
            var start=cage.HandlePosition;
            // NativeTissueSimulation's same conversion: anatomy-frame delta -> world
            // vector -> raw imported local vector -> source-body meters.
            var anatomyDirection=path==0?Vector3.forward:Vector3.right;
            var worldDelta=fixture.root.transform.TransformVector(anatomyDirection);
            var localDirection=moving.ToMeters(moving.transform.InverseTransformVector(worldDelta)).normalized;
            Assert(TissueCage.Finite(localDirection)&&localDirection.sqrMagnitude>.99f,"synthetic tracked-tool trajectory has valid body-meter direction");
            float pull=Mathf.Min(.006f,cage.Preset.maxDisplacement*.5f);
            int originalHandle=cage.Handle,totalConstraints=0,rejectedPairs=0;
            float peakResidual=0,peakMotion=0,holdResidual=0;double elapsed=0;
            for(int tick=0;tick<72;tick++)
            {
                float fraction=Mathf.Min(1,(tick+1)/36f);
                var target=start+localDirection*(pull*fraction);
                foreach(var tissue in fixture.bodies)tissue.Step(1f/90,tissue==moving?target:Vector3.zero);
                var watch=System.Diagnostics.Stopwatch.StartNew();fixture.contact.Solve(true);watch.Stop();elapsed+=watch.Elapsed.TotalMilliseconds;
                totalConstraints+=fixture.contact.AppliedConstraints;rejectedPairs+=fixture.contact.RejectedPairs;
                peakResidual=Mathf.Max(peakResidual,fixture.contact.MaximumExcessResidualMeters);
                Assert(cage.Handle==originalHandle,"moving organ keeps its grasp through bounded contact substeps: "+Ids[index]+" path="+path+" tick="+tick);
                peakMotion=Mathf.Max(peakMotion,Vector3.Distance(cage.Positions[originalHandle],start));
                Assert(fixture.contact.ActiveBodies==3,"all imported transformed bodies participate while tracked");
                Assert(!float.IsNaN(fixture.contact.MaximumExcessResidualMeters)&&!float.IsInfinity(fixture.contact.MaximumExcessResidualMeters),"unresolved sampled contact remains finite and explicitly reported");
                foreach(var tissue in fixture.bodies)PositiveAndPinned(tissue.Cage);
                if(tick==36)RegistrationGate(fixture);
            }
            holdResidual=fixture.contact.Measure(true);
            Assert(peakMotion>.0001f,"actual accepted tissue moves; persistent grasp alone cannot pass a frozen simulation: "+Ids[index]+" path="+path);
            foreach(var tissue in fixture.bodies)tissue.CommitSurface();
            ValidateSurfaces(fixture);
            cage.ReleaseHandle();
            for(int tick=0;tick<30;tick++)
            {
                foreach(var tissue in fixture.bodies)tissue.Step(1f/90,Vector3.zero);
                fixture.contact.Solve(true);foreach(var tissue in fixture.bodies)PositiveAndPinned(tissue.Cage);
                Assert(cage.Handle==-1,"contact after release cannot reacquire the tool");
            }
            float releasedResidual=fixture.contact.Measure(true);
            foreach(var tissue in fixture.bodies)tissue.ResetTissue();fixture.contact.Solve(true);
            Assert(fixture.contact.AppliedConstraints==0&&fixture.contact.MaximumExcessResidualMeters==0,"reset returns to the authored attachment baseline without new separation forces");
            foreach(var tissue in fixture.bodies)
                for(int corner=0;corner<8;corner++)Assert(tissue.Cage.Positions[corner]==tissue.Cage.Rest[corner]&&tissue.Cage.Velocity(corner)==Vector3.zero,"reset clears accepted deformations and motion");
            Debug.Log($"SCALPAL_IMPORTED_CONTACT_MOTION id={Ids[index]} path={(path==0?"anatomy_inward":"anatomy_lateral")} targetMm={pull*1000:F3} acceptedPeakMm={peakMotion*1000:F3} peakSampledExcessMm={peakResidual*1000:F3} finalHeldExcessMm={holdResidual*1000:F3} afterReleaseExcessMm={releasedResidual*1000:F3} appliedConstraints={totalConstraints} rejectedPairSteps={rejectedPairs} editorMeanSolveMs={elapsed/72:F4} sourceImported=true syntheticInput=true questTimingMeasured=false");
        }

        static void RegistrationGate(Fixture fixture)
        {
            var positions=fixture.bodies.Select(tissue=>(Vector3[])tissue.Cage.Positions.Clone()).ToArray();
            var velocities=fixture.bodies.Select(tissue=>Enumerable.Range(0,8).Select(tissue.Cage.Velocity).ToArray()).ToArray();
            fixture.contact.Solve(false);
            Assert(fixture.contact.ActiveBodies==0&&fixture.contact.AppliedConstraints==0&&fixture.contact.MaximumExcessResidualMeters==0,"registration failure clears contact diagnostics and applies no correction");
            for(int body=0;body<fixture.bodies.Count;body++)for(int node=0;node<8;node++)
                Assert(fixture.bodies[body].Cage.Positions[node]==positions[body][node]&&fixture.bodies[body].Cage.Velocity(node)==velocities[body][node],"invalid registration cannot secretly move or damp an organ");
        }
        static void PositiveAndPinned(TissueCage cage)
        {
            for(int t=0;t<5;t++)
            {
                float Triple(Vector3[] p)=>Vector3.Dot(p[Cells[t,1]]-p[Cells[t,0]],Vector3.Cross(p[Cells[t,2]]-p[Cells[t,0]],p[Cells[t,3]]-p[Cells[t,0]]));
                Assert(Triple(cage.Rest)*Triple(cage.Positions)>0,"moving contact preserves positive cage orientation");
            }
            for(int node=0;node<8;node++)
            {
                Assert(TissueCage.Finite(cage.Positions[node])&&Vector3.Distance(cage.Positions[node],cage.Rest[node])<=cage.Preset.maxDisplacement+.00001f,"accepted cage motion is finite and remains under its authored displacement cap");
                if(cage.IsPinned(node))Assert(cage.Positions[node]==cage.Rest[node],"posterior pins stay exactly attached throughout movement");
            }
        }
        static void ValidateSurfaces(Fixture fixture)
        {
            for(int body=0;body<fixture.bodies.Count;body++)
            {
                var tissue=fixture.bodies[body];var display=tissue.GetComponent<MeshFilter>().sharedMesh;
                Assert(display==tissue.GetComponent<MeshCollider>().sharedMesh&&display!=tissue.SourceMesh,"current display/scored collider share one private surface");
                var original=fixture.originalVertices[body];var committed=display.vertices;var stillSource=tissue.SourceMesh.vertices;
                Assert(original.Length==committed.Length,"contact preserves source topology size");
                for(int vertex=0;vertex<original.Length;vertex++)
                {
                    Assert(stillSource[vertex]==original[vertex],"moving contact never writes the imported source asset");
                    Assert(Vector3.Distance(tissue.ToMeters(committed[vertex]),tissue.ToMeters(tissue.DeformSurfacePoint(original[vertex])))<.000002f,"committed surface follows the accepted cage, not tool target or stale source");
                }
                Assert(tissue.SourceMesh.triangles.SequenceEqual(fixture.originalTriangles[body]),"moving contact leaves imported triangle bindings unchanged");
            }
        }
    }
}
