using System;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Actual imported appendix/mesentery and body-event consumer; synthetic accepted/rejected
    // measurements. No physical dissection, interactive specimen or clinical geometry claim.
    public static class SurgicalActionAppearanceValidation
    {
        static int checks;
        static void Require(bool value,string message){checks++;if(!value)throw new InvalidOperationException("Action appearance: "+message);}
        public static int Run()
        {
            checks=0;var previous=EditorSceneManager.GetSceneManagerSetup();NativeTissueSimulation tissue=null;SurgicalActionAppearance view=null;
            try
            {
                var adapter=OpenSurgeryBuild.ConfigureSceneAttempt(out var session,out tissue);
                view=session.GetComponent<SurgicalActionAppearance>();if(view)view.Dispose();
                adapter.Interaction.Initialize(session.exercise,session.workbench.tools,session.patientFrame,adapter.Wound.transform,()=>true);
                Require(adapter.Interaction.ConfigureMobility(adapter.mobileOrganGroups,out var reason),"actual mobile group binds: "+reason);
                Require(OpenSurgeryAnatomy.Bind(session.anatomy,adapter.Interaction),"actual atlas longitudinal stations bind");
                Require(session.anatomy.TryGetPart("appendix",out var appendix),"actual imported appendix resolves");
                Require(session.anatomy.TryGetPart("mesoappendix",out var meso),"actual imported mesentery resolves");
                var source=appendix.GetComponent<MeshFilter>().sharedMesh;var contact=appendix.GetComponent<MeshCollider>().sharedMesh;
                var sourceVertices=source.vertices;var sourceTriangles=source.triangles;var original=appendix.GetComponent<MeshRenderer>();bool suppressed=original.forceRenderingOff;
                if(!view)view=session.gameObject.AddComponent<SurgicalActionAppearance>();view.Initialize(session,adapter.Interaction,adapter.Wound.transform);
                void Send(string instrument,string verb,string id,float distance=0,float length=0,string choice="")
                {
                    var action=adapter.Interaction.CreateMeasurement(instrument,verb,id,adapter.Wound.transform.position,"visual-fixture");
                    Require(action!=null,"synthetic measurement route ready: "+verb+" "+id);
                    action.distanceMm=distance;action.lengthMm=length;action.choice=choice;
                    Require(adapter.Interaction.SubmitMeasured(action),"actual body event accepts fixture record: "+verb+" "+id);view.Refresh();
                }
                Send("suture_tie","tie","appendix",3);
                Require(session.exercise.Body.Log.Last().outcomes.Contains("not_exposed")&&view.TieCount==0,"not-exposed tie produces no ligature");
                Send("metzenbaum_scissors","cut","appendix",4,2);
                Require(view.DivisionCount==0,"not-exposed cut produces no divided anatomy");
                foreach(var layer in new[]{"skin","fat","fascia","muscle","peritoneum"})Send("scalpel","cut",layer,0,60);
                Send("suture_tie","tie","appendix",3,0,"longitudinal_unmeasured");
                Require(view.TieCount==0,"unmeasured ligature is not shown at an invented position");
                Send("metzenbaum_scissors","cut","appendix",4,0);
                Require(session.exercise.Body.Log.Last().outcomes.Contains("no_cut")&&view.DivisionCount==0,"no-cut event creates no source division");
                Send("suture_tie","tie","appendix",3);
                Require(view.TieCount==1&&view.VisibleTieCount==1,"accepted measured appendix tie creates visible actual loop/knot/tails");
                Send("suture_tie","tie","appendix",3);
                Require(view.TieCount==1,"body-deduplicated station does not draw duplicate ligatures");
                Send("suture_tie","tie","mesoappendix",5);
                Require(view.TieCount==2,"accepted mesentery ligature has its own actual cross-section");
                Send("metzenbaum_scissors","cut","mesoappendix",10,2);
                Require(view.DivisionCount==1&&view.ProximalMesh("mesoappendix")&&view.DistalMesh("mesoappendix"),"actual mesentery mesh visibly divides into two nonempty sides");
                Send("metzenbaum_scissors","cut","appendix",4,2);
                Require(session.exercise.Body.Get("appendix","removed")==1&&view.DivisionCount==2,"actual removed fact yields retained stump and actual-appendix specimen");
                VerifyClip(view,adapter.Interaction,"appendix",source,4);
                VerifyClip(view,adapter.Interaction,"mesoappendix",meso.GetComponent<MeshFilter>().sharedMesh,10);
                var specimenView=view.DistalView("appendix");var caption=specimenView.GetComponentInChildren<TextMesh>();
                Require(caption&&caption.text=="Specimen\nAssisted display","assisted specimen notice stays explicit without a field-width single line");
                var captionBounds=caption.GetComponent<MeshRenderer>().bounds;
                Require(captionBounds.size.magnitude>0&&captionBounds.size.magnitude<.06f,"actual registered caption geometry fits inside a 60 mm field budget");
                Vector3 presentationOffset=appendix.transform.TransformVector(specimenView.localPosition);
                Require(Vector3.Distance(presentationOffset,-adapter.Wound.transform.forward*.02f)<.00001f,
                    "caption correction preserves the exact 20 mm assisted specimen offset and source pose");
                var target=adapter.Interaction.Targets.Single(t=>t.tissueId=="appendix");target.Refresh();
                Require(original.enabled&&original.forceRenderingOff&&target.Available,"divided presentation preserves original contact availability");
                Require(target.TryContact(appendix.transform.TransformPoint(source.vertices[0]),.003f,out _),"original imported appendix still accepts actual surface contact");
                Require(appendix.GetComponent<MeshFilter>().sharedMesh==source&&appendix.GetComponent<MeshCollider>().sharedMesh==contact
                    &&sourceVertices.SequenceEqual(source.vertices)&&sourceTriangles.SequenceEqual(source.triangles),"appearance never mutates original geometry or collider");
                Require(OpenSurgeryAnatomy.Bind(session.anatomy,adapter.Interaction),"source landmarks rebind with organ replacement and divided render meshes present");
                int built=view.GeometryBuilds,logCount=session.exercise.Body.Log.Count;
                for(int i=0;i<50;i++)view.Refresh();
                Require(view.GeometryBuilds==built&&session.exercise.Body.Log.Count==logCount,"idle refresh rebuilds no meshes and emits no scored actions");
                var group=adapter.Interaction.Mobility.Single(g=>g.Contains(appendix.transform));Vector3 grip=appendix.transform.TransformPoint(source.bounds.center);
                Vector3 specimenBefore=view.DistalView("appendix").position;
                Require(group.BeginHold(grip),"original organ mobilization accepts hold");group.Follow(grip+Vector3.up*.01f,.1f);view.Refresh();
                Require(Vector3.Distance(specimenBefore,view.DistalView("appendix").position)>.005f,"clipped anatomy follows actual mobile group");
                group.EndHold();group.RestoreRest();
                var root=session.patientFrame.root;Vector3 specimen=view.DistalView("appendix").position;
                root.SetPositionAndRotation(root.position+new Vector3(.4f,.1f,-.2f),Quaternion.Euler(0,43,0)*root.rotation);view.Refresh();
                Require(Vector3.Distance(specimen,view.DistalView("appendix").position)>.1f,"specimen and ties follow transformed patient registration");
                session.anatomy.SetRegistrationValid(false);view.Refresh();
                Require(!view.DivisionVisible("appendix")&&!view.DivisionVisible("mesoappendix")&&!target.Available,"invalid registration hides divisions and preserves scoring refusal");
                Require(view.VisibleTieCount==0,"invalid registration hides placed ligatures");
                session.anatomy.SetRegistrationValid(true);view.Refresh();Require(view.DivisionVisible("appendix"),"valid registration restores retained and detached meshes");
                Send("assistant","close","skin");Require(!view.DivisionVisible("appendix")&&!view.DivisionVisible("mesoappendix")&&view.VisibleTieCount==0,"closed skin hides divided anatomy and ligatures");
                var selected=session.exercise.SelectedCase;
                Require(session.exercise.SelectCase(new ScalpalBundle{cases=new[]{selected}},selected.caseId,true,out reason),"fresh attempt resets body: "+reason);
                view.Refresh();Require(view.TieCount==0&&view.DivisionCount==0&&original.forceRenderingOff==suppressed,"retry clears all transient surgery presentation and restores original draw");
                view.Dispose();Require(appendix.GetComponent<MeshFilter>().sharedMesh==source&&appendix.GetComponent<MeshCollider>().sharedMesh==contact,"disposal preserves imported geometry");
                Debug.Log("SCALPAL_SURGICAL_ACTION_APPEARANCE_OK: "+checks+" actual imported-mesh/event checks; synthetic measurements; no headset or physical specimen evidence");return checks;
            }
            finally{if(view)UnityEngine.Object.DestroyImmediate(view);if(tissue)tissue.Dispose();OpenSurgeryBuild.RestoreScenes(previous);}
        }
        static void VerifyClip(SurgicalActionAppearance view,OpenBodyInteraction input,string id,Mesh source,float mm)
        {
            var target=input.Targets.Single(t=>t.tissueId==id);Vector3 world=target.basePoint.position+target.LongitudinalWorld*mm*.001f;
            Vector3 plane=target.transform.InverseTransformPoint(world),normal=target.transform.localToWorldMatrix.transpose.MultiplyVector(target.LongitudinalWorld).normalized;
            Mesh near=view.ProximalMesh(id),far=view.DistalMesh(id);float tolerance=source.bounds.size.magnitude*.00001f;
                Require(near&&far&&near.vertexCount>3&&far.vertexCount>3,id+": both clipped source surfaces contain geometry");
                Require(near.subMeshCount==2&&far.subMeshCount==2&&near.GetTriangles(1).Length>=3&&far.GetTriangles(1).Length>=3,
                    id+": both measured cut stations have explicit fresh-cut caps");
            Require(near.vertices.All(v=>Vector3.Dot(v-plane,normal)<=tolerance),id+": retained geometry lies only on proximal station side");
            Require(far.vertices.All(v=>Vector3.Dot(v-plane,normal)>=-tolerance),id+": specimen geometry lies only on distal station side");
            double originalArea=Area(source),nearArea=Area(near),farArea=Area(far);
            Require(originalArea>0&&nearArea>originalArea*.001&&farArea>originalArea*.001,id+": both sides have nonzero source-derived area");
            Require(nearArea+farArea>=originalArea*.999,id+": clipping preserves the original source surface area plus cut caps");
            Require(near.vertices.Any(v=>Mathf.Abs(Vector3.Dot(v-plane,normal))<=tolerance)&&far.vertices.Any(v=>Mathf.Abs(Vector3.Dot(v-plane,normal))<=tolerance),id+": retained and distal cut faces meet measured station");
        }
        static double Area(Mesh mesh){var v=mesh.vertices;var t=mesh.triangles;double area=0;for(int i=0;i<t.Length;i+=3)area+=Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]).magnitude*.5;return area;}
    }
}
