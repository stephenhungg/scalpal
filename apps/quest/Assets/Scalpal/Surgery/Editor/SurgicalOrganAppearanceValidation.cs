using System;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Quest;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Imported cecum + actual native session and mobilization consumer; synthetic Editor
    // transforms. Verifies presentation/contact separation, not clinical shape or headset feel.
    public static class SurgicalOrganAppearanceValidation
    {
        static int checks;
        static void Require(bool value,string message){checks++;if(!value)throw new InvalidOperationException("Organ appearance: "+message);}
        public static int Run()
        {
            checks=0;var previous=EditorSceneManager.GetSceneManagerSetup();NativeTissueSimulation tissue=null;
            SurgicalOrganAppearance appearance=null;
            try
            {
                var adapter=OpenSurgeryBuild.ConfigureSceneAttempt(out var session,out tissue);
                appearance=session.GetComponent<SurgicalOrganAppearance>();
                if(appearance)appearance.Dispose();
                Require(session.anatomy.TryGetPart("cecum",out var part),"actual imported cecum resolves");
                var source=part.GetComponent<MeshFilter>();var original=part.GetComponent<MeshRenderer>();var collider=part.GetComponent<MeshCollider>();
                Require(source&&source.sharedMesh&&original&&collider,"original imported renderer and measured collider exist");
                var originalMesh=source.sharedMesh;var contactMesh=collider.sharedMesh;var vertices=originalMesh.vertices;var triangles=originalMesh.triangles;
                int logCount=session.exercise.Body.Log.Count;string stableId=part.stableId;
                bool colliderEnabled=collider.enabled, suppressed=original.forceRenderingOff;
                if(!appearance)appearance=session.gameObject.AddComponent<SurgicalOrganAppearance>();appearance.Initialize(session,adapter.Interaction);
                var shown=appearance.PresentationRenderer;var visual=appearance.PresentationMesh;
                Require(shown&&visual&&visual!=originalMesh,"a distinct render-only mesh is composed");
                Require(source.sharedMesh==originalMesh&&collider.sharedMesh==contactMesh&&collider.enabled==colliderEnabled,"original measured mesh/collider unchanged");
                Require(vertices.SequenceEqual(originalMesh.vertices)&&triangles.SequenceEqual(originalMesh.triangles),"source vertices and triangles unchanged");
                Require(part.stableId==stableId&&session.exercise.Body.Log.Count==logCount,"identity and event log unchanged");
                Require(shown.GetComponentsInChildren<Collider>(true).Length==0,"visual child creates no scored or physical collider");
                Bounds bounds=originalMesh.bounds;Vector3 half=bounds.extents;
                foreach(var vertex in visual.vertices)
                {
                    Vector3 v=vertex-bounds.center;
                    float r=v.x*v.x/(half.x*half.x)+v.y*v.y/(half.y*half.y)+v.z*v.z/(half.z*half.z);
                    Require(r<=1.00001f,"visual vertex stays inside the original ellipsoid envelope");
                }
                Require(visual.subMeshCount==2&&visual.GetTriangles(1).Length>0,"three longitudinal taenia sectors have visible material geometry");
                var faces=visual.triangles;var shape=visual.vertices;
                for(int i=0;i<faces.Length;i+=3)
                {
                    Vector3 a=shape[faces[i]],b=shape[faces[i+1]],c=shape[faces[i+2]];
                    Require(Vector3.Dot(Vector3.Cross(b-a,c-a),(a+b+c)/3-bounds.center)>0,"every presentation face points outward for opaque rendering");
                }
                int axis=half.x>half.y&&half.x>half.z?0:half.y>half.z?1:2;
                var surface=visual.vertices;
                var ratios=Enumerable.Range(1,31).Select(row=>{
                    Vector3 v=surface[row*49]-bounds.center;float t=v[axis]/half[axis];
                    int radial=(axis+1)%3;return Mathf.Abs(v[radial])/half[radial]/Mathf.Sqrt(1-t*t);
                }).ToArray();
                Require(ratios.Max()-ratios.Min()>.06f,"haustral profile varies beyond a smooth ellipsoid");
                session.anatomy.SetRegistrationValid(true);appearance.Refresh();
                Require(shown.enabled&&original.enabled&&original.forceRenderingOff&&part.HasVisibleGeometry,"replacement renders while original visibility/contact semantics remain valid");
                var target=adapter.Interaction.Targets.Single(t=>t.tissueId=="cecum");target.Refresh();
                Require(target.Available,"actual imported cecum remains available to scored contact with replacement drawing");
                Vector3 contact=part.transform.TransformPoint(originalMesh.vertices[0]);
                Require(target.TryContact(contact,.003f,out _),"original cecum surface still accepts real contact after presentation refresh");
                var group=adapter.Interaction.Mobility.Single(g=>g.Contains(part.transform));
                Vector3 grip=part.transform.TransformPoint(originalMesh.bounds.center), before=shown.bounds.center;
                Require(group.BeginHold(grip),"actual mobilization accepts the original cecum grip");
                group.Follow(grip+Vector3.up*.01f,.1f);appearance.Refresh();
                Require(Vector3.Distance(before,shown.bounds.center)>.005f,"render child follows actual organ mobilization");
                Require(source.sharedMesh==originalMesh&&collider.sharedMesh==contactMesh,"mobilization preserves original scored geometry");
                group.EndHold();group.RestoreRest();
                var root=session.patientFrame.root;Vector3 rootPosition=root.position;Quaternion rootRotation=root.rotation;
                Vector3 point=shown.transform.TransformPoint(visual.bounds.center);
                root.SetPositionAndRotation(rootPosition+new Vector3(.3f,.2f,-.1f),Quaternion.Euler(0,37,0)*rootRotation);appearance.Refresh();
                Require(Vector3.Distance(point,shown.transform.TransformPoint(visual.bounds.center))>.1f,"presentation follows moved/rotated patient registration");
                session.anatomy.SetRegistrationValid(false);appearance.Refresh();
                Require(!shown.enabled&&!original.enabled&&!collider.enabled,"invalid registration hides presentation and honors original anatomy gate");
                Require(!target.Available,"invalid registration still refuses scored contact");
                session.anatomy.SetRegistrationValid(true);appearance.Refresh();
                Require(shown.enabled&&original.forceRenderingOff,"valid registration restores replacement without drawing both surfaces");
                part.SetGhosted(true);appearance.Refresh();
                Require(!shown.enabled&&!original.forceRenderingOff,"explicit ghost mode retains the existing original ghost renderer");
                part.SetGhosted(false);appearance.Refresh();
                Require(shown.enabled&&original.forceRenderingOff,"opaque replacement returns after ghost inspection");
                appearance.Dispose();
                Require(original.forceRenderingOff==suppressed&&source.sharedMesh==originalMesh&&collider.sharedMesh==contactMesh,"disposal restores original draw state and preserves contact");
                Require(session.exercise.Body.Log.Count==logCount,"all visual changes emit no scored actions");
                Debug.Log("SCALPAL_SURGICAL_ORGAN_APPEARANCE_OK: "+checks+" source-envelope/render-only checks; imported cecum, actual mobilization, synthetic Editor transforms; no headset/clinical validation");
                return checks;
            }
            finally{if(appearance)UnityEngine.Object.DestroyImmediate(appearance);if(tissue)tissue.Dispose();OpenSurgeryBuild.RestoreScenes(previous);}
        }
    }
}
