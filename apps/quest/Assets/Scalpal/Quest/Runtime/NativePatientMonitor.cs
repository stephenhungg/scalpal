using System;
using System.Globalization;
using Scalpal.Anatomy.Tissue;
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
        public string HeartRateValue {get;private set;}="HR --";
        public string PressureValue {get;private set;}="BP --/--";
        public string OxygenValue {get;private set;}="SpO2 --";
        public string RespirationValue {get;private set;}="RR --";
        public string StateValue {get;private set;}="No signal";
        public string SourceValue {get;private set;}="Simulated, not real vitals";
        // Why the screen shows no signal; kept for diagnostics, never drawn in the learner's view.
        public string StatusReason {get;private set;}="";
        public LineRenderer Trace=>trace;
        public string Outcome=>HasFreshSample&&condition!=null?condition.outcome.result:"";
        // A terminal outcome stays on screen after practice stops or the coach stops polling.
        public bool Holding=>HasFreshSample&&condition!=null&&condition.outcome.result!="in_progress";
        bool Frozen=>HasFreshSample&&frozen;
        public const int TracePoints=320;
        public const float TraceSeconds=4,TraceBaseline=.08f,TraceAmplitude=.055f;
        CoachSnapshotState observed;
        CoachPatientCondition condition;
        string scopeSid="",scopePatient="",scopeProcedure="",scopeCase="",scopeMode="";
        double nextLayout;
        int version=-1;
        double receivedAt=double.NegativeInfinity;
        GameObject view;
        TextMeshPro heartRate,pressure,oxygen,respiration,state,source;
        LineRenderer trace;
        Vector3[] tracePositions;
        Renderer pole,standBase;
        Material housingMaterial,screenMaterial;
        double frozenAt;
        bool frozen;
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
                // The monitor stands at the head of the table with the patient; it shows while the patient is
                // in the room, and keeps a terminal outcome (death, early end) on screen after practice stops.
                var frame=session.patientFrame;
                // In full VR the authored patient is always on the table, so the monitor stands there from OR entry (dashes until
                // the coach streams vitals). In AR it waits for the body fit, since the frame means nothing before it.
                bool present=!frame||frame.gameObject.activeInHierarchy||session.PresentationMode=="virtual";
                // The aligned floor, not the tracking origin: VR locomotion raises and lowers the origin.
                if(frame&&present)Place(frame,session.workbench?session.workbench.initialHeadFloorPosition.y:0);
                if(Time.realtimeSinceStartupAsDouble>=nextLayout)
                {nextLayout=Time.realtimeSinceStartupAsDouble+.5;ScalpalBrandLayout.SizeForViewer(view.transform,viewer.transform.position);}
                view.SetActive((present||Holding)&&Vector3.Distance(view.transform.position,viewer.transform.position)>=.2f);
                if(view.activeSelf)DrawTrace(Time.realtimeSinceStartupAsDouble);
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
            if(!ready){observed=snapshot;if(Holding)return;Invalidate("Paused: tracking, body fit or coach unavailable");return;}
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
            {if(Holding)return;Invalidate("Awaiting a fresh coach sample");return;}
            if(HasFreshSample&&!newSample)return;
            HasFreshSample=true;Flatline=condition.outcome.result=="died";
            if(condition.outcome.result!="ended")frozen=false;
            else if(!frozen){frozen=true;frozenAt=now;}
            var v=condition.vitals;
            HeartRateValue=Flatline?"HR 0":"HR "+Number(v.hr);
            PressureValue="BP "+Number(v.sys)+"/"+Number(v.dia);
            OxygenValue="SpO2 "+(v.spo2<=0?"--":Number(v.spo2)+"%");
            RespirationValue="RR "+Number(v.rr);
            StateValue=State(condition);
            SourceValue="Simulated from "+Short(condition.baselineSource,10)+" baseline";
            StatusReason="";
            DisplayText=HeartRateValue+" bpm | "+PressureValue+" mmHg | "+OxygenValue+" | "+RespirationValue+"/min | "+StateValue+" | "+SourceValue;
            ApplyText();
        }
        // One plain word or two for the learner; the class and causes stay with Jarvis and the dashboard.
        static string State(CoachPatientCondition c)
        {
            switch(c.outcome.result)
            {
                case "died":return "Asystole";
                case "ended":return "Case ended";
                case "completed":return "Complete";
            }
            return c.vitals.hemorrhageClass<=1?"Stable":"Bleeding "+Mathf.RoundToInt(c.vitals.bloodLossPct).ToString(CultureInfo.InvariantCulture)+"%";
        }
        static string Number(float value)=>value.ToString("0",CultureInfo.InvariantCulture);
        static string Short(string value,int maximum)=>value.Length<=maximum?value:value.Substring(0,maximum-3)+"...";
        void Unavailable(string reason)
        {
            HasFreshSample=false;Flatline=false;frozen=false;StatusReason=reason;
            HeartRateValue="HR --";PressureValue="BP --/--";OxygenValue="SpO2 --";RespirationValue="RR --";StateValue="No signal";
            SourceValue="Simulated, not real vitals";
            DisplayText=reason+" | "+HeartRateValue+" | "+PressureValue+" | "+OxygenValue+" | "+RespirationValue+" | "+StateValue+" | "+SourceValue;
            ApplyText();
        }
        void ApplyText()
        {
            Set(heartRate,HeartRateValue);Set(pressure,PressureValue);Set(oxygen,OxygenValue);Set(respiration,RespirationValue);
            Set(state,StateValue);Set(source,SourceValue);
            if(trace)trace.enabled=HasFreshSample;
        }
        static void Set(TextMeshPro text,string value)
        {if(text&&text.text!=value){text.text=value;text.GetComponent<ScalpalTextFit>().Fit();}}
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
            // Until a patient frame places it, stand in front of the viewer (Editor fixtures, no patient).
            view.transform.position=viewer.position+viewer.rotation*new Vector3(.95f,-.1f,1.55f);
            var facing=view.transform.position-viewer.position;facing.y=0;
            view.transform.rotation=Quaternion.LookRotation(facing.sqrMagnitude>1e-6f?facing:Vector3.forward,Vector3.up);
            // +Z points away from the learner; the screen faces -Z.
            housingMaterial=TissueRuntimeMaterial.Create("MonitorHousing",new Color(.11f,.115f,.12f));
            screenMaterial=TissueRuntimeMaterial.Create("MonitorScreen",new Color(.006f,.008f,.008f));
            screenMaterial.SetFloat("_Glossiness",.6f);
            Part(PrimitiveType.Cube,"MonitorHousing",new Vector3(0,0,.035f),new Vector3(.80f,.54f,.06f),housingMaterial);
            Part(PrimitiveType.Cube,"MonitorScreen",new Vector3(0,0,.003f),new Vector3(.73f,.47f,.004f),screenMaterial);
            Part(PrimitiveType.Cube,"MonitorMount",new Vector3(0,-.24f,.085f),new Vector3(.12f,.10f,.05f),housingMaterial);
            pole=Part(PrimitiveType.Cylinder,"MonitorStandPole",new Vector3(0,-.9f,.085f),new Vector3(.045f,.6f,.045f),housingMaterial);
            standBase=Part(PrimitiveType.Cylinder,"MonitorStandBase",new Vector3(0,-1.5f,.085f),new Vector3(.46f,.012f,.46f),housingMaterial);
            var brand=ScalpalBrand.Active;const float z=-.004f,left=-.33f,middle=.02f,right=.33f;
            brand.Text(view.transform,"MonitorTitle","Simulated",ScalpalTextRole.Body,new Vector3(left,.205f,z),.03f,.30f,.05f);
            state=brand.Text(view.transform,"MonitorState",StateValue,ScalpalTextRole.Label,new Vector3(right,.205f,z),.03f,.36f,.07f,TextAnchor.UpperRight);
            heartRate=brand.Text(view.transform,"MonitorHeartRate",HeartRateValue,ScalpalTextRole.Label,new Vector3(left,.025f,z),.05f,.33f,.08f);
            heartRate.color=ScalpalBrand.SurgicalGreen;
            oxygen=brand.Text(view.transform,"MonitorOxygen",OxygenValue,ScalpalTextRole.Label,new Vector3(middle,.025f,z),.05f,.31f,.08f);
            pressure=brand.Text(view.transform,"MonitorPressure",PressureValue,ScalpalTextRole.Label,new Vector3(left,-.075f,z),.05f,.33f,.08f);
            respiration=brand.Text(view.transform,"MonitorRespiration",RespirationValue,ScalpalTextRole.Label,new Vector3(middle,-.075f,z),.05f,.31f,.08f);
            source=brand.Text(view.transform,"MonitorSource",SourceValue,ScalpalTextRole.Body,new Vector3(left,-.165f,z),.026f,.66f,.05f);
            var line=new GameObject("EcgTrace");line.transform.SetParent(view.transform,false);trace=line.AddComponent<LineRenderer>();
            trace.useWorldSpace=false;trace.positionCount=TracePoints;trace.startWidth=trace.endWidth=.004f;trace.sharedMaterial=brand.ray;
            trace.startColor=trace.endColor=ScalpalBrand.SurgicalGreen;trace.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;trace.receiveShadows=false;
            tracePositions=new Vector3[TracePoints];
            ApplyText();DrawTrace(0);
            ScalpalBrandLayout.SizeForViewer(view.transform,viewer.position);
        }
        Renderer Part(PrimitiveType shape,string name,Vector3 position,Vector3 scale,Material material)
        {
            var part=GameObject.CreatePrimitive(shape);part.name=name;part.transform.SetParent(view.transform,false);
            part.transform.localPosition=position;part.transform.localScale=scale;
            var collider=part.GetComponent<Collider>();if(collider)DestroyImmediate(collider); // furniture only, never a contact target
            var renderer=part.GetComponent<Renderer>();renderer.sharedMaterial=material;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            return renderer;
        }
        // Patient frame: +X patient left, +Y anterior, +Z cranial. The learner stands at the patient's right side,
        // so the monitor stands at the head of the table on the patient's left, its screen turned to the learner.
        public void Place(Transform patientFrame,float floorY)
        {
            if(!view||!patientFrame)return;
            var screen=patientFrame.TransformPoint(new Vector3(.62f,0,.80f));screen.y=patientFrame.position.y+.45f;
            var facing=screen-patientFrame.TransformPoint(new Vector3(-.65f,0,0));facing.y=0;
            if(facing.sqrMagnitude<1e-6f)return;
            view.transform.SetPositionAndRotation(screen,Quaternion.LookRotation(facing,Vector3.up));
            // The stand reaches the floor from wherever the screen is (AR rooms differ in table height).
            float drop=Mathf.Max(.3f,screen.y-floorY);
            pole.transform.localPosition=new Vector3(0,-.27f-(drop-.27f)*.5f,.085f);pole.transform.localScale=new Vector3(.045f,(drop-.27f)*.5f,.045f);
            standBase.transform.localPosition=new Vector3(0,-drop+.012f,.085f);
        }
        // ECG lead II shape on the server's heart rate: newest sample at the right, four seconds across.
        // Asystole draws a flat line; an ended case freezes the last trace; no signal hides it.
        public void DrawTrace(double now)
        {
            if(!trace)return;
            double t=Frozen?frozenAt:now;
            float hr=HasFreshSample&&condition!=null&&!Flatline?condition.vitals.hr:0;
            for(int i=0;i<TracePoints;i++)
            {
                double sample=t-TraceSeconds*(TracePoints-1-i)/(TracePoints-1);
                tracePositions[i]=new Vector3(-.33f+.66f*i/(TracePoints-1),TraceBaseline+TraceAmplitude*Ecg(sample,hr),-.004f);
            }
            trace.SetPositions(tracePositions);
        }
        public static float Ecg(double time,float heartRate)
        {
            if(!(heartRate>0)||float.IsInfinity(heartRate))return 0;
            double beat=60.0/heartRate,since=time-Math.Floor(time/beat)*beat;
            float squeeze=(float)Math.Min(1,beat/.6),s=(float)since/squeeze;
            return Wave(s,.08f,.025f,.12f)+Wave(s,.17f,.008f,-.12f)+Wave(s,.20f,.011f,1f)+Wave(s,.23f,.01f,-.25f)+Wave(s,.40f,.045f,.28f);
        }
        static float Wave(float at,float center,float width,float height){float d=(at-center)/width;return height*Mathf.Exp(-d*d);}
        void OnDestroy()
        {
            if(housingMaterial){if(Application.isPlaying)Destroy(housingMaterial);else DestroyImmediate(housingMaterial);}
            if(screenMaterial){if(Application.isPlaying)Destroy(screenMaterial);else DestroyImmediate(screenMaterial);}
        }
    }
}
