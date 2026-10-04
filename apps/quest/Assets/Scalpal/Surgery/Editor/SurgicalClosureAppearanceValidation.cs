using System;
using System.Linq;
using Scalpal.Quest;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    public static class SurgicalClosureAppearanceValidation
    {
        public static void Run()
        {
            var previous=EditorSceneManager.GetSceneManagerSetup(); NativeTissueSimulation tissue=null; int checks=0;
            void Require(bool valid,string message){checks++;if(!valid)throw new InvalidOperationException("Closure appearance: "+message);}
            try
            {
                var adapter=OpenSurgeryBuild.ConfigureSceneAttempt(out var session,out tissue);
                session.presentation.passthrough=false;session.presentation.Apply();
                adapter.Interaction.Initialize(session.exercise,session.workbench.tools,session.patientFrame,adapter.Wound.transform,()=>true);
                Require(adapter.Interaction.ConfigureMobility(adapter.mobileOrganGroups,out var mobility),"actual mobile organ group binds: "+mobility);
                var appearance=session.GetComponent<SurgicalClosureAppearance>();
                appearance.Initialize(session,adapter.Interaction,adapter.Wound.transform);appearance.Refresh();
                Require(!appearance.ClosureVisible,"intact skin shows no postoperative seam");
                var originalParts=adapter.Interaction.Mobility.SelectMany(g=>g.Parts).Distinct().ToArray();
                Require(originalParts.Length>=4,"closure exercises the real delivered cecum/appendix/mesentery/artery group, never an empty fixture");
                var positions=originalParts.Select(p=>p.position).ToArray();
                var meshes=originalParts.Select(p=>p.GetComponent<MeshFilter>().sharedMesh).ToArray();
                var contact=originalParts.Select(p=>p.GetComponent<MeshCollider>().sharedMesh).ToArray();
                var action=adapter.Interaction.CreateMeasurement("assistant","close","skin",adapter.Wound.transform.position,"closure-appearance-fixture");
                Require(action!=null&&adapter.Interaction.SubmitMeasured(action),"real body-event route accepts authored assistant closure");
                int log=session.exercise.Body.Log.Count;appearance.Refresh();
                Require(appearance.ClosureVisible&&appearance.ConcealedRendererCount>=originalParts.Length,"closure seam visible and delivered contents concealed");
                var view=adapter.Wound.GetComponentsInChildren<LineRenderer>(true).Where(l=>l.transform.parent&&l.transform.parent.name=="AssistedClosure_SeamAndInterruptedStitches").ToArray();
                Require(view.Length==7&&view.Sum(l=>l.positionCount)>40,"one apposed seam and six interrupted sutures exist; lines="+view.Length+" positions="+view.Sum(l=>l.positionCount)+" names="+string.Join(",",view.Select(l=>l.name)));
                for(int i=0;i<originalParts.Length;i++)
                {
                    Require(originalParts[i].position==positions[i],"closure drawing does not teleport anatomy");
                    Require(originalParts[i].GetComponent<MeshFilter>().sharedMesh==meshes[i]&&originalParts[i].GetComponent<MeshCollider>().sharedMesh==contact[i],"closure drawing preserves measured mesh/collider");
                    Require(originalParts[i].GetComponent<MeshRenderer>().forceRenderingOff,"delivered anatomy does not protrude through closed skin");
                }
                session.anatomy.SetRegistrationValid(false);appearance.Refresh();Require(!appearance.ClosureVisible,"invalid registration hides seam");
                session.anatomy.SetRegistrationValid(true);appearance.Refresh();Require(appearance.ClosureVisible,"restored registration restores same seam");
                Require(session.exercise.Body.Log.Count==log,"presentation emits no fabricated events");
                var selected=session.exercise.SelectedCase;
                Require(session.exercise.SelectCase(new Scalpal.Exercises.Data.ScalpalBundle{cases=new[]{selected}},selected.caseId,true,out _),"retry creates real new attempt");
                session.anatomy.SetRegistrationValid(true);appearance.Refresh();
                Require(!appearance.ClosureVisible&&appearance.ConcealedRendererCount==0,"retry clears seam and restores source drawing policy");
                Debug.Log("SCALPAL_SURGICAL_CLOSURE_APPEARANCE_OK: "+checks+" actual-scene synthetic body-event/render checks; no tissue healing or physical suturing claim");
            }
            finally{if(tissue)tissue.Dispose();OpenSurgeryBuild.RestoreScenes(previous);}
        }
    }
}
