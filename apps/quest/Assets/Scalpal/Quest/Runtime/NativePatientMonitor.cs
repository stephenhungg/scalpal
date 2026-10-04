using System;
using System.Globalization;
using Scalpal.Brand;
using Scalpal.Exercises.Coach;
using Scalpal.Instruments;
using UnityEngine.XR;
using TMPro;
using UnityEngine;

namespace Scalpal.Quest
{
    // Uses the relay's existing accepted alert feed. No HTTP, camera, physiology,
    // baseline capture, scoring, progression or real-volunteer vital extrapolation.
    [DefaultExecutionOrder(150)]
    public sealed class NativePatientMonitor : MonoBehaviour
    {
        public NativeCaseSession session;
        public const double FreshSeconds=3;
        public string DisplayText {get;private set;}="Simulated monitor unavailable";
        public bool Flatline {get;private set;}
        public bool HasFreshSample {get;private set;}
        public Transform ViewRoot=>view?view.transform:null;
        CoachSnapshotState observed;
        CoachPatientCondition condition;
        string scopeSid="",scopePatient="",scopeProcedure="",scopeCase="",scopeMode="";
        double nextLayout;
        int version=-1;
        double receivedAt=double.NegativeInfinity;
        GameObject view;
        TextMeshPro body;
        LineRenderer flatline;
        bool focused=true;

        void Awake(){if(!session)session=GetComponent<NativeCaseSession>();}
        public bool HeadsetInputReady=>focused&&XRInput.Source.DisplayRunning&&XRInput.Source.FloorTracking&&XRInput.Source.HasFocus&&XRInput.TryPose(XRNode.Head,out _);
        public void Initialize(NativeCaseSession source){session=source;ResetMonitor();}
        public void ResetMonitor()
        {scopeSid=scopePatient=scopeProcedure=scopeCase=scopeMode="";observed=null;condition=null;version=-1;receivedAt=double.NegativeInfinity;Unavailable("Awaiting a fresh coach sample");}
        void OnDisable(){ResetMonitor();if(view)view.SetActive(false);}
        void OnApplicationFocus(bool value){focused=value;if(!value)Invalidate("Paused: headset focus lost");}
        void OnApplicationPause(bool value){if(value)Invalidate("Paused: headset suspended");}
        void Invalidate(string reason){condition=null;receivedAt=double.NegativeInfinity;Unavailable(reason);}
        void LateUpdate()
        {
            if(!session)return;
            var coach=session.coach;
            var viewer=session.workbench?session.workbench.headCamera:null;
            if(viewer)EnsureView(viewer.transform);
            bool ready=HeadsetInputReady&&session.workbench&&session.workbench.IsReady&&session.Practicing&&session.anatomy&&session.anatomy.RegistrationValid&&session.RegistrationReady&&coach&&coach.IsSynchronized;
            TickMonitor(coach?coach.AlertSnapshot:null,coach?coach.SessionId:"",coach?coach.SessionPatientId:"",
                coach?coach.SessionProcedureId:"",coach?coach.SessionCaseId:"",session.PresentationMode,ready,Time.realtimeSinceStartupAsDouble);
            if(viewer&&view)
            {
                if(Time.realtimeSinceStartupAsDouble>=nextLayout)
                {nextLayout=Time.realtimeSinceStartupAsDouble+.5;ScalpalBrandLayout.SizeForViewer(view.transform,viewer.transform.position);}
                if(Vector3.Distance(view.transform.position,viewer.transform.position)<.2f)view.SetActive(false);
                else view.SetActive(true);
            }
        }
        // Receipt time is local monotonic time: the coach snapshot has no generatedAt.
        // Repeated use of the same object does not renew freshness. A newly published
        // poll may legitimately have the same version while the patient is unchanged.
        public void TickMonitor(CoachSnapshotState snapshot,string sid,string patient,string procedure,string caseId,string mode,bool ready,double now)
        {
            if(sid!=scopeSid||patient!=scopePatient||procedure!=scopeProcedure||caseId!=scopeCase||mode!=scopeMode)
            {ResetMonitor();scopeSid=sid;scopePatient=patient;scopeProcedure=procedure;scopeCase=caseId;scopeMode=mode;}
            if(double.IsNaN(now)||double.IsInfinity(now)||string.IsNullOrEmpty(sid)||string.IsNullOrEmpty(patient)||string.IsNullOrEmpty(procedure)||string.IsNullOrEmpty(caseId)||
                (mode!="virtual"&&mode!="mixed_reality"))
            {observed=snapshot;Invalidate("No matched coach session");return;}
            if(!ready){observed=snapshot;Invalidate("Paused: tracking, body fit or coach unavailable");return;}
            bool newSample=!ReferenceEquals(snapshot,observed);
            if(newSample)
            {
                observed=snapshot;
                if(snapshot==null||snapshot.sessionId!=sid||snapshot.patientId!=patient||snapshot.procedureId!=procedure||snapshot.caseId!=caseId||snapshot.mode!=mode||
                    snapshot.version<0||snapshot.version<version||!ValidCondition(snapshot.condition))
                {Invalidate("Rejected mismatched or malformed coach sample");return;}
                if(snapshot.status=="paused"){Invalidate("Paused by coach");return;}
                if(snapshot.status!="active"&&snapshot.status!="completed"){Invalidate("Unknown coach state");return;}
                condition=snapshot.condition;version=snapshot.version;receivedAt=now;
            }
            if(condition==null||now<receivedAt||now-receivedAt>FreshSeconds)
            {Invalidate("Awaiting a fresh coach sample");return;}
            if(HasFreshSample&&!newSample)return;
            HasFreshSample=true;Flatline=condition.outcome.result=="died";
            var v=condition.vitals;
            string source=Short(condition.baselineSource,26);
            DisplayText="HR "+Number(v.hr)+" bpm | BP "+Number(v.sys)+"/"+Number(v.dia)+" mmHg\n"
                +"RR "+Number(v.rr)+"/min | SpO2 "+(v.spo2<0?"unavailable":Number(v.spo2)+"%")+"\n"
                +"Loss "+v.bloodLossPct.ToString("0.0",CultureInfo.InvariantCulture)+"% | Class "+v.hemorrhageClass+"\n"
                +"Raw loss "+Number(condition.rawBloodLossMl)+" ml | demo x"+Number(v.scale)+"\n"
                +"Baseline: "+source+" | simulated delta\n"
                +"Outcome: "+condition.outcome.result.Replace('_',' ')+(Flatline?" (simulated)":"")+"\n"
                +(Flatline?"Cause: "+Short(condition.outcome.cause,58)+"\n":"")+"Not real volunteer vitals";
            ApplyText();
        }
        static string Number(float value)=>value.ToString("0.#",CultureInfo.InvariantCulture);
        static string Short(string value,int maximum)=>value.Length<=maximum?value:value.Substring(0,maximum-3)+"...";
        void Unavailable(string reason)
        {HasFreshSample=false;Flatline=false;DisplayText=reason+"\nHR -- | BP -- | RR --\nSpO2 unavailable\nSimulated patient display\nNot real volunteer vitals";ApplyText();}
        void ApplyText()
        {if(body&&body.text!=DisplayText){body.text=DisplayText;body.GetComponent<ScalpalTextFit>().Fit();}if(flatline)flatline.enabled=Flatline;}
        public static bool ValidCondition(CoachPatientCondition c)
        {
            var v=c?.vitals;var o=c?.outcome;
            if(v==null||o==null||!v.simulated||string.IsNullOrWhiteSpace(c.baselineSource)||string.IsNullOrWhiteSpace(v.label)||v.baseline==null||
                !Finite(c.weightKg)||c.weightKg<=0||!Finite(c.rawBloodLossMl)||c.rawBloodLossMl<0||!Finite(v.scale)||v.scale<=0||
                !Finite(v.hr)||!Finite(v.rr)||!Finite(v.sys)||!Finite(v.dia)||!Finite(v.spo2)||!Finite(v.bloodLossPct)||v.bloodLossPct<0||
                v.hemorrhageClass<1||v.hemorrhageClass>4||(v.spo2!=-1&&(v.spo2<0||v.spo2>100))||
                !Finite(v.baseline.hr)||v.baseline.hr<=0||!Finite(v.baseline.rr)||v.baseline.rr<=0||string.IsNullOrEmpty(v.baseline.source)||
                (o.result!="in_progress"&&o.result!="completed"&&o.result!="ended"&&o.result!="died"))return false;
            if(o.result=="died")return v.hr==0&&v.rr==0&&v.sys==0&&v.dia==0&&!string.IsNullOrWhiteSpace(o.cause)&&!string.IsNullOrWhiteSpace(o.at);
            return v.hr>0&&v.rr>0&&v.sys>0&&v.dia>0;
        }
        static bool Finite(float n)=>!float.IsNaN(n)&&!float.IsInfinity(n);
        public static bool TryDecodeFeed(string json,string sid,out CoachAlertFeed feed)
        {
            feed=null;
            try{if(!string.IsNullOrWhiteSpace(json))feed=JsonUtility.FromJson<CoachAlertFeed>(json);}
            catch(ArgumentException){return false;}
            return CoachRelay.ValidAlertFeed(feed,sid,0)&&ValidCondition(feed.snapshot.condition);
        }
        public void EnsureView(Transform viewer)
        {
            if(view||!viewer)return;
            view=new GameObject("SimulatedPatientMonitor");view.transform.SetParent(transform,false);
            view.transform.position=viewer.position+viewer.rotation*new Vector3(.95f,.03f,1.55f);
            view.transform.rotation=Quaternion.LookRotation(view.transform.position-viewer.position,Vector3.up);
            var plate=GameObject.CreatePrimitive(PrimitiveType.Cube);plate.name="MonitorGlass";plate.transform.SetParent(view.transform,false);
            plate.transform.localScale=new Vector3(1.78f,.77f,.014f);
            var collider=plate.GetComponent<Collider>();if(collider)DestroyImmediate(collider);
            plate.GetComponent<Renderer>().sharedMaterial=ScalpalBrand.Active.glass;
            var brand=ScalpalBrand.Active;float distance=Vector3.Distance(view.transform.position,viewer.position);
            brand.Text(view.transform,"MonitorTitle","SIMULATED PATIENT",ScalpalTextRole.Label,new Vector3(-.83f,.34f,-.015f),ScalpalBrandLayout.Em(brand,ScalpalTextRole.Label,distance),1.66f,.08f);
            body=brand.Text(view.transform,"MonitorValues",DisplayText,ScalpalTextRole.Body,new Vector3(-.83f,.245f,-.015f),ScalpalBrandLayout.Em(brand,ScalpalTextRole.Body,distance),1.66f,.60f);
            var line=new GameObject("ServerOutcomeFlatline");line.transform.SetParent(view.transform,false);flatline=line.AddComponent<LineRenderer>();
            flatline.useWorldSpace=false;flatline.positionCount=2;flatline.SetPosition(0,new Vector3(-.8f,-.345f,-.018f));flatline.SetPosition(1,new Vector3(.8f,-.345f,-.018f));
            flatline.startWidth=flatline.endWidth=.003f;flatline.sharedMaterial=brand.ray;flatline.startColor=flatline.endColor=ScalpalBrand.Ink70;flatline.enabled=false;
            ApplyText();
            ScalpalBrandLayout.SizeForViewer(view.transform,viewer.position);
        }
    }
}
