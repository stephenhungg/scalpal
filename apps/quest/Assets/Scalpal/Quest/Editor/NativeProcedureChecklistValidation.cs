using System;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Brand;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest.Editor
{
    // Actual packaged case/reducer and production read-only UI; XR tracking and
    // session bindings are synthetic. Not Play Mode or a physical headset result.
    public static class NativeProcedureChecklistValidation
    {
        sealed class Input:IXRInputSource
        {
            public bool running=true,focused=true,head=true;
            public bool DisplayRunning=>running;
            public bool FloorTracking=>true;
            public bool HasFocus=>focused;
            public bool TryPose(XRNode node,out Pose pose){pose=Pose.identity;return node!=XRNode.Head||head;}
            public float Grip(XRNode node)=>0;
            public float Trigger(XRNode node)=>0;
            public bool Button(XRNode node,XRInputButton button)=>false;
        }
        static int checks;
        static void Require(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException("Procedure checklist: "+message);}
        static void Set(object instance,string name,object value)=>instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(instance,value);
        static void SetProperty(object instance,string name,object value)=>instance.GetType().GetProperty(name).GetSetMethod(true).Invoke(instance,new[]{value});
        [MenuItem("Scalpal/Quest/Validate Procedure Checklist")]
        public static void Run()
        {
            checks=0;
            var asset=Resources.Load<TextAsset>("scalpal_bundle");Require(asset,"actual packaged case data exists");
            var bundle=JsonUtility.FromJson<ScalpalBundle>(asset.text);
            var selected=bundle.cases.First(item=>item.procedureId=="open_appendectomy");
            var procedure=selected.procedure;
            Require(procedure?.openBody?.milestones?.Length==10,"actual open appendectomy has ten checklist goals");
            var fixture=new GameObject("ReadOnlyProcedureChecklistFixture");
            var oldInput=XRInput.Source;
            try
            {
                var owner=fixture.AddComponent<NativeCaseSession>();
                var rig=fixture.AddComponent<NativeWorkbench>();owner.workbench=rig;
                rig.headCamera=new GameObject("ChecklistViewer").AddComponent<Camera>();rig.headCamera.transform.SetParent(fixture.transform,false);
                rig.headCamera.transform.SetPositionAndRotation(new Vector3(12,.7f,-4),Quaternion.Euler(13,39,-11));
                owner.anatomy=fixture.AddComponent<AnatomyController>();owner.anatomy.SetPreviewMode(false);owner.anatomy.SetRegistrationValid(true);
                var binding=fixture.AddComponent<AnatomyExerciseBinding>();owner.exercise=binding;binding.anatomy=owner.anatomy;
                Set(binding,"selectedAnatomy",owner.anatomy);Set(binding,"selectedCase",selected);binding.requireCoachSynchronization=false;
                var runner=new CaseRunner(procedure);Set(binding,"runner",runner);
                SetProperty(rig,"IsReady",true);SetProperty(owner,"Phase","Practicing");
                var checklist=fixture.AddComponent<NativeProcedureChecklist>();checklist.Initialize(owner);
                var input=new Input();XRInput.Source=input;
                void Refresh(){checklist.Simulate();}
                Refresh();Require(checklist.Visible&&checklist.Rows.Count==10,"practice displays the complete checklist, not only the next step");
                Require(checklist.Rows.Select(row=>row.Id).SequenceEqual(procedure.openBody.milestones.Select(m=>m.id)),"rows retain authored milestone ordering");
                Require(checklist.Rows.Select(row=>row.Id).Distinct().Count()==10,"stable checklist row identities are unique");
                foreach(var row in checklist.Rows)
                    Require(row.Title==procedure.steps.Single(step=>step.id==row.Id).title&&!row.Completed,"fresh rows use actual authored titles and unmet body facts");
                Require(checklist.VisualRoot.GetComponentsInChildren<Collider>(true).Length==0,"guidance has no colliders to block or consume tool input");
                Require(checklist.VisualRoot.GetComponentsInChildren<TextMeshPro>(true).Select(text=>text.name).SequenceEqual(new[]{"ChecklistTitle"}.Concat(checklist.Rows.Select(row=>"ChecklistRow_"+row.Id))),
                    "learner checklist shows only the procedure title and its steps, with no legend or explanatory caption");
                AssertReadability(checklist,rig.headCamera.transform);

                // Closure is a later goal accepted by the actual body reducer without
                // earlier marking. Unlike cutCoverage, it does not require a measured
                // marking denominator. Use that real off-order engine behavior.
                foreach(var action in CaseRunner.PerfectEvents(procedure.steps.Single(step=>step.id=="close")))runner.Handle(action);
                Refresh();
                Require(!checklist.Rows.Single(row=>row.Id=="mark_incision").Completed&&checklist.Rows.Single(row=>row.Id=="close").Completed,"accepted later milestone ticks while earlier marking remains unmet");
                Require(runner.Current.id=="mark_incision","current-next-step index cannot explain the later check mark");
                Require(runner.OrderDeviations.Contains("close"),"real reducer records off-order completion without the UI restricting it");
                var facts=runner.Body.Facts.ToDictionary(pair=>pair.Key,pair=>pair.Value);
                int logs=runner.Body.Log.Count,mistakes=runner.Mistakes.Count,achieved=runner.Achieved.Count;
                for(int i=0;i<5;i++)Refresh();
                Require(runner.Body.Log.Count==logs&&runner.Mistakes.Count==mistakes&&runner.Achieved.Count==achieved
                    &&facts.Count==runner.Body.Facts.Count&&facts.All(pair=>runner.Body.Facts.TryGetValue(pair.Key,out var value)&&value==pair.Value),"rendering creates no actions, facts, mistakes or progression");

                runner=new CaseRunner(procedure);Set(binding,"runner",runner);Refresh();
                Require(checklist.Rows.All(row=>!row.Completed),"retry replaces body identity and clears previous check marks");
                foreach(var action in CaseRunner.PerfectEvents(procedure.steps.Single(step=>step.id=="close")))
                {var rejected=action;rejected.evidence.registered=false;runner.Handle(rejected);}
                Refresh();Require(checklist.Rows.All(row=>!row.Completed)&&runner.Body.Log.Count==0,"unregistered requested actions cannot tick rows");

                // Accepted achievement history matches the coach/dashboard checklist.
                // New bleeding changes current guidance and removes its done check,
                // but it does not erase the previously accepted milestone history.
                runner=new CaseRunner(procedure);Set(binding,"runner",runner);
                foreach(var step in procedure.steps.Take(9))foreach(var action in CaseRunner.PerfectEvents(step))runner.Handle(action);
                Refresh();Require(checklist.Rows.Single(row=>row.Id=="divide_mesoappendix").Completed,"accepted engine history marks the completed hemostasis goal");
                Require(NativeProcedureChecklist.ReadRows(procedure,runner.Body,Array.Empty<string>(),runner.Current?.id).All(row=>!row.Completed),"satisfied body predicates cannot fabricate completed markers when authoritative history is empty");
                Require(NativeProcedureChecklist.ReadRows(procedure,runner.Body,null,runner.Current?.id).Length==0,"missing authoritative milestone history fails closed without a predicate fallback");
                runner.Handle(CaseEvent.Surgery(new BodyAction{actionId="checklist-new-bleed",verb="cut",tissueId="appendicular_artery",layer="appendicular_artery",instrumentId="scalpel",instrumentInstanceId="checklist-scalpel",registered=true,timeMs=1000,lengthMm=4}));
                Refresh();var recheck=checklist.Rows.Single(row=>row.Id=="divide_mesoappendix");
                Require(!recheck.Completed&&recheck.Current&&recheck.Achieved&&runner.CompletedMilestones.Contains("divide_mesoappendix"),"rebleed excludes current guidance from the done check while retaining achieved history");
                var changed=procedure.openBody.milestones.Single(m=>m.id=="divide_mesoappendix");
                Require(changed.predicates.Any(predicate=>!runner.Body.Test(predicate))&&runner.Body.Get("","activeBleeds")>0
                    &&runner.Current.id=="divide_mesoappendix","live body/current guidance still reports the new unmet safety condition");
                Require(checklist.VisualRoot.GetComponentsInChildren<TextMeshPro>(true).Single(text=>text.name=="ChecklistRow_divide_mesoappendix").text.StartsWith("[>] ",StringComparison.Ordinal),"the current step is marked in its own row");
                AssertReadability(checklist,rig.headCamera.transform);

                void Hidden(Action invalidate,Action restore,string message)
                {invalidate();Refresh();Require(!checklist.Visible,message);restore();Refresh();Require(checklist.Visible,"valid readiness restores checklist without altering body");}
                Hidden(()=>input.head=false,()=>input.head=true,"head tracking loss hides guidance");
                Hidden(()=>input.focused=false,()=>input.focused=true,"XR focus loss hides guidance");
                Hidden(()=>input.running=false,()=>input.running=true,"display shutdown hides guidance");
                Hidden(()=>owner.anatomy.SetRegistrationValid(false),()=>owner.anatomy.SetRegistrationValid(true),"anatomy registration loss hides guidance");
                Hidden(()=>SetProperty(rig,"IsReady",false),()=>SetProperty(rig,"IsReady",true),"scene/rig readiness loss hides guidance");
                Hidden(()=>SetProperty(owner,"Phase","Recap"),()=>SetProperty(owner,"Phase","Practicing"),"leaving practice hides guidance");
                Hidden(()=>owner.enabled=false,()=>owner.enabled=true,"disabled session cannot leave stale guidance visible");
                Hidden(()=>checklist.enabled=false,()=>checklist.enabled=true,"component disable hides guidance immediately");

                var presentation=fixture.AddComponent<NativePresentation>();owner.presentation=presentation;
                var registration=fixture.AddComponent<NativeBodyRegistration>();owner.bodyRegistration=registration;
                presentation.passthrough=true;SetProperty(registration,"Accepted",true);Set(registration,"candidateValid",true);Set(registration,"observationTime",Time.realtimeSinceStartup);
                Refresh();Require(checklist.Visible,"AR uses the same current-body checklist after accepted fresh fit");
                Hidden(()=>Set(registration,"candidateValid",false),()=>{Set(registration,"candidateValid",true);Set(registration,"observationTime",Time.realtimeSinceStartup);},"invalid AR body fit hides checklist");
                presentation.passthrough=false;Refresh();Require(checklist.Visible,"VR restores the identical shared checklist without body registration");

                var legacy=bundle.cases.First(item=>item.procedureId=="lap_appendectomy");
                Set(binding,"selectedCase",legacy);Set(binding,"runner",new CaseRunner(legacy.procedure));Refresh();
                Require(!checklist.Visible&&checklist.Rows.Count==0,"case change to legacy procedure removes incompatible old checklist rows");
                Set(binding,"selectedCase",selected);Set(binding,"runner",new CaseRunner(procedure));Refresh();
                Require(checklist.Visible&&checklist.Rows.All(row=>!row.Completed),"returning to an open-body case starts a fresh view");
                var bad=JsonUtility.FromJson<Procedure>(JsonUtility.ToJson(procedure));bad.openBody.milestones[1].id=bad.openBody.milestones[0].id;
                Require(NativeProcedureChecklist.ReadRows(bad,new BodyState(bad.openBody.tissues),Array.Empty<string>(),bad.firstStep).Length==0,"duplicate authored row identity fails closed");
                Debug.Log($"SCALPAL_NATIVE_PROCEDURE_CHECKLIST_VALIDATION_OK checks={checks} actualCase=open_appendectomy checklistAuthority=CompletedMilestonesMinusCurrent readOnly=true syntheticTracking=true physicalHeadset=false");
            }
            finally{XRInput.Source=oldInput;UnityEngine.Object.DestroyImmediate(fixture);}
        }
        static void AssertReadability(NativeProcedureChecklist checklist,Transform viewer)
        {
            foreach(var measurement in ScalpalBrandLayout.Measure(checklist.VisualRoot.transform,viewer.position))
                Require(measurement.Passes,"Brand line floor measured in view: "+measurement.fit.name+" mmAt1m="+measurement.mmAt1m);
            foreach(var text in checklist.VisualRoot.GetComponentsInChildren<TextMeshPro>(true))
            {
                Require(text.font==ScalpalBrand.Active.Font(text.name=="ChecklistTitle"?ScalpalTextRole.Title:ScalpalTextRole.Body),"actual UI uses shared Brand static TMP fonts");
                var position=viewer.InverseTransformPoint(text.transform.position);
                Require(position.x<0&&position.y<.5f&&position.z>0,"checklist is anchored in the top-left field of view");
            }
        }
    }
}
