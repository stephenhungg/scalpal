using System;
using System.Collections.Generic;
using Scalpal.Brand;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // Read-only guidance. Accepted engine milestone history owns the check marks,
    // matching the coach/dashboard contract. The current guidance row is excluded
    // from done checks even if it was achieved earlier (for example, a rebleed).
    // This view never emits actions, chooses a next step, scores or restricts a tool.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(195)]
    public sealed class NativeProcedureChecklist : MonoBehaviour
    {
        public const string GuidanceCaption="guidance — any tool/action remains available";
        public readonly struct Row
        {
            public readonly string Id, Title;
            public readonly bool Completed, Achieved, Current;
            public Row(string id,string title,bool achieved,bool current)
            {Id=id;Title=title;Achieved=achieved;Current=current;Completed=achieved&&!current;}
        }
        public IReadOnlyList<Row> Rows=>rows;
        public GameObject VisualRoot=>view;
        public bool Visible=>view&&view.activeInHierarchy;
        NativeCaseSession session;
        Procedure shownProcedure;
        BodyState shownBody;
        Row[] rows=Array.Empty<Row>();
        TextMeshPro[] rowText=Array.Empty<TextMeshPro>();
        GameObject view;
        Mesh glassMesh;
        bool paused,focused=true;
        const float Width=.92f,RowSpacing=.052f;

        void Start(){var owner=GetComponent<NativeCaseSession>();if(owner)Initialize(owner);}
        public void Initialize(NativeCaseSession owner){DisposeView();session=owner;}
        void LateUpdate()=>Simulate();
        public void Simulate()
        {
            var rig=session?session.workbench:null;
            var exercise=session?session.exercise:null;
            if(!isActiveAndEnabled||!session||!session.isActiveAndEnabled||!session.Practicing||!rig||!rig.IsReady||!rig.headCamera
                ||paused||!focused||!XRInput.Source.DisplayRunning||!XRInput.Source.HasFocus||!XRInput.TryPose(XRNode.Head,out _)
                ||!session.RegistrationReady||!session.anatomy||!session.anatomy.RegistrationValid||!exercise||!exercise.CanScore)
            {Hide();return;}
            RefreshView(exercise.SelectedCase?.procedure,exercise.Body,exercise.CompletedMilestones,exercise.Current?.id,rig.headCamera.transform);
        }
        // Data-to-view boundary, also used by Editor fixtures with actual CaseRunner
        // state and synthetic poses. It cannot mutate the runner/body/session.
        public void RefreshView(Procedure procedure,BodyState body,IReadOnlyCollection<string> completedMilestones,string currentStepId,Transform viewer)
        {
            if(!viewer||body==null||completedMilestones==null||procedure?.openBody?.version!=1||!ValidPlan(procedure))
            {DisposeView();return;}
            if(!ReferenceEquals(procedure,shownProcedure)||!ReferenceEquals(body,shownBody)||!view)
            {
                DisposeView();shownProcedure=procedure;shownBody=body;
                rows=ReadRows(procedure,body,completedMilestones,currentStepId);BuildView(procedure,viewer);
            }
            view.transform.SetPositionAndRotation(viewer.position,viewer.rotation);view.SetActive(true);
            for(int i=0;i<rows.Length;i++)
            {
                bool achieved=Achieved(completedMilestones,rows[i].Id);
                bool current=string.Equals(rows[i].Id,currentStepId,StringComparison.Ordinal);
                if(rows[i].Achieved!=achieved||rows[i].Current!=current)
                {
                    rows[i]=new Row(rows[i].Id,rows[i].Title,achieved,current);
                    rowText[i].text=Line(i,rows[i]);rowText[i].color=rows[i].Completed||current?ScalpalBrand.Ink:ScalpalBrand.Ink70;
                    rowText[i].GetComponent<ScalpalTextFit>().Fit();
                }
            }
        }
        public static Row[] ReadRows(Procedure procedure,BodyState body,IReadOnlyCollection<string> completedMilestones,string currentStepId)
        {
            if(body==null||completedMilestones==null||!ValidPlan(procedure))return Array.Empty<Row>();
            var milestones=procedure.openBody.milestones;var result=new Row[milestones.Length];
            for(int i=0;i<result.Length;i++)
            {
                string title=milestones[i].id.Replace('_',' ');
                foreach(var step in procedure.steps??Array.Empty<ProcedureStep>())
                    if(step?.id==milestones[i].id&&!string.IsNullOrWhiteSpace(step.title)){title=step.title;break;}
                result[i]=new Row(milestones[i].id,title,Achieved(completedMilestones,milestones[i].id),string.Equals(milestones[i].id,currentStepId,StringComparison.Ordinal));
            }
            return result;
        }
        static bool ValidPlan(Procedure procedure)
        {
            if(procedure?.openBody?.version!=1||procedure.openBody.milestones==null||procedure.openBody.milestones.Length==0)return false;
            var milestones=procedure.openBody.milestones;
            for(int i=0;i<milestones.Length;i++)
            {
                var milestone=milestones[i];
                if(milestone==null||string.IsNullOrWhiteSpace(milestone.id)||milestone.predicates==null||milestone.predicates.Length==0)return false;
                for(int j=0;j<i;j++)if(string.Equals(milestone.id,milestones[j].id,StringComparison.Ordinal))return false;
            }
            return true;
        }
        static bool Achieved(IReadOnlyCollection<string> completedMilestones,string id)
        {
            foreach(var achieved in completedMilestones)if(string.Equals(achieved,id,StringComparison.Ordinal))return true;
            return false;
        }
        static string Line(int index,Row row)=>(row.Current?"[>] ":row.Completed?"[x] ":"[ ] ")+(index+1)+". "+row.Title;
        void BuildView(Procedure procedure,Transform viewer)
        {
            view=new GameObject("ProcedureChecklistGuidance");
            view.transform.SetPositionAndRotation(viewer.position,viewer.rotation);
            var brand=ScalpalBrand.Active;
            const float left=-.68f,top=.43f,depth=1.25f;
            float height=.21f+rows.Length*RowSpacing;
            var glass=new GameObject("ChecklistGlass",typeof(MeshFilter),typeof(MeshRenderer));glass.transform.SetParent(view.transform,false);
            glass.transform.localPosition=new Vector3(left+Width*.5f,top-height*.5f+.015f,depth+.01f);
            glassMesh=new Mesh{name="ChecklistReadOnlyGlass"};
            float x=(Width+.05f)*.5f,y=height*.5f;
            glassMesh.vertices=new[]{new Vector3(-x,-y,0),new Vector3(-x,y,0),new Vector3(x,y,0),new Vector3(x,-y,0)};
            glassMesh.triangles=new[]{0,1,2,0,2,3};glassMesh.RecalculateNormals();glassMesh.RecalculateBounds();
            glass.GetComponent<MeshFilter>().sharedMesh=glassMesh;glass.GetComponent<MeshRenderer>().sharedMaterial=brand.glass;
            var renderer=glass.GetComponent<MeshRenderer>();renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;renderer.receiveShadows=false;
            brand.Text(view.transform,"ChecklistTitle",procedure.shortTitle??procedure.title??"procedure",ScalpalTextRole.Title,new Vector3(left,top,depth),.045f,Width,.065f,TextAnchor.UpperLeft,true);
            brand.Text(view.transform,"ChecklistHistoryLabel","x = completed | > = current guidance",ScalpalTextRole.Caption,
                new Vector3(left,top-.068f,depth),.031f,Width,.055f,TextAnchor.UpperLeft,true);
            rowText=new TextMeshPro[rows.Length];
            for(int i=0;i<rows.Length;i++)
            {
                rowText[i]=brand.Text(view.transform,"ChecklistRow_"+rows[i].Id,Line(i,rows[i]),ScalpalTextRole.Body,
                    new Vector3(left,top-.12f-i*RowSpacing,depth),.031f,Width,.055f,TextAnchor.UpperLeft,true);
                rowText[i].color=rows[i].Completed||rows[i].Current?ScalpalBrand.Ink:ScalpalBrand.Ink70;
            }
            brand.Text(view.transform,"ChecklistGuidance",GuidanceCaption,ScalpalTextRole.Caption,
                new Vector3(left,top-.13f-rows.Length*RowSpacing,depth),.031f,Width,.055f,TextAnchor.UpperLeft,true);
            ScalpalBrandLayout.SizeForViewer(view.transform,viewer.position);
        }
        public void Hide(){if(view)view.SetActive(false);}
        void OnApplicationPause(bool value){paused=value;if(value)Hide();}
        void OnApplicationFocus(bool value){focused=value;if(!value)Hide();}
        void OnDisable()=>Hide();
        void OnDestroy()=>DisposeView();
        void DisposeView()
        {
            if(view){view.SetActive(false);if(Application.isPlaying)Destroy(view);else DestroyImmediate(view);}
            if(glassMesh){if(Application.isPlaying)Destroy(glassMesh);else DestroyImmediate(glassMesh);}
            view=null;glassMesh=null;shownProcedure=null;shownBody=null;
            rows=Array.Empty<Row>();rowText=Array.Empty<TextMeshPro>();
        }
    }
}
