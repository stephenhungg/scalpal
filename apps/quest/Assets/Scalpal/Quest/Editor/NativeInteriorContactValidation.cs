using System;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Geometry/input component regressions. Actual Play Mode startup/trigger delivery is a separate gate.
    public static class NativeInteriorContactValidation
    {
        static int checks;
        [MenuItem("Scalpal/Quest/Validate Interior Tip Contact")]
        public static void Run()
        {
            checks = 0;
            var root = new GameObject("InteriorContactRegression");
            var inactive = new GameObject("InactiveHardwareFixture"); inactive.SetActive(false);
            Mesh mesh = null;
            try
            {
                var controller = root.AddComponent<AnatomyController>(); controller.initiallyHiddenSystems = Array.Empty<string>();
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube); body.name = "anat_appendix";
                body.transform.SetParent(root.transform, false);
                UnityEngine.Object.DestroyImmediate(body.GetComponent<BoxCollider>());
                mesh = UnityEngine.Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh);
                body.GetComponent<MeshFilter>().sharedMesh = mesh;
                var shell = body.AddComponent<MeshCollider>(); shell.sharedMesh = mesh; shell.convex = false;
                var part = body.AddComponent<AnatomyPart>(); part.stableId = "appendix";
                var helper = new ClosedMeshInterior(); Physics.SyncTransforms();
                var point = new Vector3(.11f, .13f, -.07f);
                Assert(helper.Contains(shell, point), "closed cube interior is accepted");
                Assert(!helper.Contains(shell, new Vector3(2,0,0)), "exterior rejected");
                root.transform.SetPositionAndRotation(new Vector3(3,-2,4), Quaternion.Euler(35,71,19));
                root.transform.localScale = new Vector3(.23f,.37f,.19f); Physics.SyncTransforms();
                Assert(helper.Contains(shell, shell.transform.TransformPoint(point)), "rotated translated nonuniform frame");
                Assert(!helper.Contains(shell, point), "old world point is outside moved body");
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); root.transform.localScale = Vector3.one;
                var originalVertices = mesh.vertices; var originalTriangles = mesh.triangles;
                var moved = (Vector3[])originalVertices.Clone();
                for (int i=0;i<moved.Length;i++) moved[i] = moved[i] * .12f + Vector3.right * .3f;
                mesh.vertices = moved; mesh.RecalculateBounds(); shell.sharedMesh = null; shell.sharedMesh = mesh; Physics.SyncTransforms();
                Assert(!helper.Contains(shell, point), "same-mesh deformed old interior rejected");
                Assert(helper.Contains(shell, Vector3.right * .3f), "same-mesh current deformed interior accepted");
                mesh.vertices = originalVertices;
                var open = new int[originalTriangles.Length-3]; Array.Copy(originalTriangles, open, open.Length);
                mesh.triangles = open; mesh.RecalculateBounds();
                Assert(!helper.Contains(shell, point), "open shell fails closed after same-mesh topology edit");
                var reversed = (int[])originalTriangles.Clone();
                for(int i=0;i<reversed.Length;i+=3) { int a=reversed[i]; reversed[i]=reversed[i+1]; reversed[i+1]=a; }
                mesh.triangles = reversed;
                Assert(!helper.Contains(shell, point), "inward-wound shell rejected");
                var nonmanifold = new int[originalTriangles.Length+3]; Array.Copy(originalTriangles,nonmanifold,originalTriangles.Length);
                Array.Copy(originalTriangles,0,nonmanifold,originalTriangles.Length,3); mesh.triangles=nonmanifold;
                Assert(!helper.Contains(shell,point),"non-manifold duplicate face rejected");
                mesh.triangles = originalTriangles; mesh.RecalculateBounds(); shell.sharedMesh = null; shell.sharedMesh = mesh;
                Assert(helper.Contains(shell, point), "valid topology restoration recovers");
                var inverted = (Vector3[])originalVertices.Clone();
                for(int i=0;i<inverted.Length;i++) inverted[i].x = -inverted[i].x;
                mesh.vertices=inverted;
                Assert(!helper.Contains(shell,point),"same-topology face inversion rejected");
                mesh.vertices=originalVertices; mesh.RecalculateBounds();
                for(int i=0;i<8;i++) helper.Contains(shell, point);
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                int acceptedWarm = 0;
                for(int i=0;i<256;i++) if(helper.Contains(shell, point)) acceptedWarm++;
                long warmBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Assert(acceptedWarm==256 && warmBytes==0,"warm current-mesh queries allocate zero managed bytes; observed="+warmBytes);
                shell.enabled = false; Assert(!helper.Contains(shell, point), "disabled shell rejected"); shell.enabled = true;

                var toolObject = new GameObject("scalpel"); toolObject.transform.SetParent(root.transform, false);
                var tool = toolObject.AddComponent<InstrumentBehaviour>(); tool.instrumentId = "scalpel";
                var tipObject = new GameObject("Tip"); tipObject.transform.SetParent(toolObject.transform, false);
                var tipCollider = tipObject.AddComponent<SphereCollider>(); tipCollider.radius = .012f; tipCollider.isTrigger = true;
                var contact = tipObject.AddComponent<InstrumentTipContact>();
                tool.actionPoint = tipObject.transform; tool.contactRadius = .012f;
                tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1);
                toolObject.transform.position = point;
                Physics.SyncTransforms();
                Assert(!Physics.ComputePenetration(tipCollider,tipObject.transform.position,tipObject.transform.rotation,
                    shell,shell.transform.position,shell.transform.rotation,out _,out _), "fixture truly misses hollow-shell PhysX contact");
                var workbench = inactive.AddComponent<NativeWorkbench>(); workbench.tools = new[] { tool };
                typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(workbench,new object[]{true});
                var exercise = root.AddComponent<AnatomyExerciseBinding>(); exercise.anatomy = controller;
                var candidate = new SurgicalCase { caseId="interior-fixture",patientId="synthetic",procedureId="interior",status="ready",
                    instruments=new[]{new Instrument{id="scalpel"}}, procedure=new Procedure {id="interior",firstStep="apply",structures=new[]{"appendix"},
                        steps=new[]{new ProcedureStep{id="apply",instrumentId="scalpel",targets=new[]{"appendix"},
                            check=new SuccessCheck{type="apply_count",targets=new[]{"appendix"},count=8}}}}};
                controller.RebuildIndex();
                Assert(exercise.SelectCase(new ScalpalBundle{cases=new[]{candidate}},candidate.caseId,false,out var reason), "fixture selection: "+reason);
                controller.SetRegistrationValid(true); bool practice=true;
                var input = root.AddComponent<NativeProcedureInput>(); input.Initialize(exercise,workbench,()=>practice);
                int events=0; exercise.EventHandled += (_,__)=>events++;
                practice=false; input.ProbeInteriorContacts(); Assert(events==0,"phase blocks interior score"); practice=true;
                controller.SetRegistrationValid(false); input.ProbeInteriorContacts(); Assert(events==0,"registration blocks interior score"); controller.SetRegistrationValid(true);
                tool.SetTrackingValid(false); input.ProbeInteriorContacts(); Assert(events==0,"tool tracking blocks interior score"); tool.SetTrackingValid(true); tool.SetActivation(1);
                part.SetVisible(false); input.ProbeInteriorContacts(); Assert(events==0,"hidden geometry blocks interior score"); part.SetVisible(true);
                toolObject.transform.position = Vector3.right*2; Physics.SyncTransforms();
                Assert(!contact.TryReportContact(shell), "rejected exterior callback not acknowledged");
                toolObject.transform.position=point; Physics.SyncTransforms();
                input.ProbeInteriorContacts(); Assert(events==1,"interior activation scores through existing adapter after rejected contact without rearming");
                input.ProbeInteriorContacts(); Assert(events==1,"continuous interior contact counts once");
                contact.ResetContactCycle(); Assert(!contact.TryReportContact(shell)&&events==1,"adapter also deduplicates accepted cycle");
                tool.SetActivation(0); Tick(input); tool.SetActivation(1);
                toolObject.transform.position=Vector3.right*2; Physics.SyncTransforms(); input.ProbeInteriorContacts(); Assert(events==1,"outside current mesh cannot score");
                toolObject.transform.position=point; Physics.SyncTransforms(); input.ProbeInteriorContacts(); Assert(events==2,"fresh interior activation scores once");
                tool.SetActivation(0); Tick(input); tool.SetActivation(1);
                var foreign = GameObject.CreatePrimitive(PrimitiveType.Cube); foreign.name="anat_appendix"; foreign.transform.SetParent(root.transform,false);
                Assert(!contact.TryReportContact(foreign.GetComponent<Collider>())&&events==2,"same-name foreign collider cannot score");
                Debug.Log("SCALPAL_NATIVE_INTERIOR_VALIDATION_OK checks="+checks+" geometry/input fixtures; not headset or Play Mode startup evidence");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(inactive); if(mesh) UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static void Tick(NativeProcedureInput input) => typeof(NativeProcedureInput).GetMethod("Update",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(input,null);
        static void Assert(bool value,string message) { if(!value)throw new InvalidOperationException("Interior contact regression: "+message); checks++; }
    }
}
