using System;
using System.IO;
using System.Linq;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Preop;
using UnityEngine;

namespace Scalpal.Shell
{
    public sealed class HubController : MonoBehaviour
    {
        public Font font;
        public Material glass, buttonMaterial, textMaterial, accent;
        public Transform content;
        public ShellInput input;
        public ScalpalPreopService service;
        public ExplorePatientModel Model { get; private set; } = new ExplorePatientModel();
        public bool Exploring { get; private set; }
        public bool Transitioning { get; private set; }
        public ShellButton BeginButton { get; private set; }
        Transform launch, explore, cards, detail, filters;
        TextMesh hint, banner, summary;
        ShellButton retryButton;
        string notice = "Loading patient records…", retrying = "";
        float retryAt;
        bool initialized, wired, offlineSeen;
        public bool CanBegin => Model.CanBegin && !offlineSeen && service && !service.IsOffline;
        [Serializable] sealed class EndpointConfig { public string coachBaseUrl, encounterBaseUrl; }
        void Awake() { Initialize(); }
        void Start()
        {
            ConfigureEndpoint(); Wire(); Reload();
            ShellPause.Ensure().Configure(input);
            input.Confirm = () => { if (!Exploring) Enter(); };
            if(ShellPause.ReturningToExplore) { ShellPause.ReturningToExplore=false;Enter(); }
        }
        public void Initialize()
        {
            if (initialized) return; initialized = true;
            ShellView.Configure(font, glass, buttonMaterial, textMaterial, accent);
            launch = ShellView.Panel(content, "Launch", new Vector3(0, 0, 1.3f), new Vector2(.86f,.48f));
            ShellView.Text(launch, "S C A L P A L", new Vector3(0,.14f,-.008f), .065f,.72f,TextAnchor.MiddleCenter);
            ShellView.Text(launch, "Diagnose and operate on synthetic patients", new Vector3(0,.055f,-.008f), .032f,.76f,TextAnchor.MiddleCenter);
            ShellView.Button(launch, "Start", new Vector3(0,-.055f,-.008f),new Vector2(.30f,.075f),Enter);
            hint = ShellView.Text(launch,"Press Enter · point and pull trigger",new Vector3(0,-.145f,-.008f),.027f,.72f,TextAnchor.MiddleCenter);
            explore = new GameObject("Explore").transform; explore.SetParent(content,false); explore.localPosition = new Vector3(0,0,1.3f);
            ShellView.Panel(explore,"ExploreGlass",new Vector3(0,-.015f,.012f),new Vector2(.986f,1.06f));
            ShellView.Text(explore,"Explore patients",new Vector3(-.45f,.40f,-.01f),.044f,.6f);
            ShellView.Text(explore,"Synthetic record",new Vector3(.45f,.40f,-.01f),.026f,.29f,TextAnchor.UpperRight);
            filters = Child(explore,"Filters"); cards = Child(explore,"PatientCards"); detail = Child(explore,"PatientDetail");
            banner = ShellView.Text(explore,"",new Vector3(-.45f,-.37f,-.01f),.025f,.80f);
            summary = ShellView.Text(explore,"",new Vector3(-.45f,-.31f,-.01f),.021f,.8f);
            ShellView.Button(explore,"Refresh",new Vector3(.37f,-.43f,-.012f),new Vector2(.17f,.045f),Reload);
            ShellView.Text(explore,"Explore  /  Office  /  OR  /  Replay  /  Recap",new Vector3(-.45f,-.44f,-.01f),.022f,.69f);
            RenderFilters(); RenderCards(); RenderDetail(); explore.gameObject.SetActive(false);
        }
        static Transform Child(Transform parent,string name) { var child=new GameObject(name).transform;child.SetParent(parent,false);return child; }
        void ConfigureEndpoint()
        {
            if (!Debug.isDebugBuild) return;
            string path=Path.Combine(Application.persistentDataPath,"session-config.json");
#if UNITY_ANDROID && !UNITY_EDITOR
            using(var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using(var activity=player.GetStatic<AndroidJavaObject>("currentActivity"))
            using(var files=activity.Call<AndroidJavaObject>("getFilesDir")) path=Path.Combine(files.Call<string>("getAbsolutePath"),"session-config.json");
#endif
            if (!File.Exists(path)) return;
            try { var c=JsonUtility.FromJson<EndpointConfig>(File.ReadAllText(path)); var url=string.IsNullOrWhiteSpace(c.encounterBaseUrl)?c.coachBaseUrl:c.encounterBaseUrl; service.Configure(url); }
            catch(Exception) { notice="Service configuration invalid. Using default endpoint."; }
        }
        void Wire()
        {
            if(wired) return; wired=true;
            service.BundleLoaded += BundleLoaded; service.PatientsLoaded += PatientsLoaded; service.BriefLoaded += BriefLoaded;
            service.CaseLoaded += CaseLoaded; service.RequestFailedForRoute += Failed;service.OfflineModeChanged += OfflineChanged;
        }
        void OnDestroy()
        {
            if(!wired || !service) return;
            service.BundleLoaded -= BundleLoaded; service.PatientsLoaded -= PatientsLoaded; service.BriefLoaded -= BriefLoaded;
            service.CaseLoaded -= CaseLoaded; service.RequestFailedForRoute -= Failed;service.OfflineModeChanged -= OfflineChanged;
        }
        void Update()
        {
            if(hint) hint.text = input && input.IsHands ? "Point and pinch to start" : "Point and pull trigger · Enter in editor";
            if(retryButton)
            {
                int seconds=Mathf.CeilToInt(Mathf.Max(0,retryAt-Time.unscaledTime));
                retryButton.interactable=retrying.Length==0&&seconds==0;
                retryButton.label.text=retrying.Length>0?"Retrying…":seconds>0?"Try again in "+seconds+"s":"Try again";
            }
            RefreshBanner();
        }
        void RefreshBanner() { if(banner)banner.text=(offlineSeen || service && service.IsOffline ? "Offline data · " : "")+notice; }
        void OfflineChanged(bool value) { if(value)offlineSeen=true;RenderDetail(); }
        public void Enter() { Initialize(); Exploring=true; launch.gameObject.SetActive(false); explore.gameObject.SetActive(true); }
        public void Reload()
        {
            if(!service || Transitioning) return;
            Wire(); service.StopAllCoroutines(); retrying=""; offlineSeen=false; Model.ClearSelection();
            notice="Loading patient records…"; RenderDetail(); service.LoadBundle(); service.LoadPatients();
        }
        public void BundleLoaded(ScalpalBundle bundle) { Model.ApplyBundle(bundle); notice=bundle==null?"Bundle unavailable. Refresh to retry.":"Select a chart to explore."; RenderCards(); }
        public void PatientsLoaded(PatientList list) { Model.ApplyPatients(list); notice=list?.patients==null?"Patient list unavailable. Refresh to retry.":"Select a chart to explore."; RenderCards(); RenderDetail(); }
        public void BriefLoaded(PreopBrief brief) { if(Model.ApplyBrief(brief)) RenderDetail(); }
        void CaseLoaded(SurgicalCase value)
        {
            if(value==null) return; Model.ApplyCase(value);
            if(retrying==value.patientId) { retrying="";retryAt=Time.unscaledTime+Mathf.Max(1,value.retryAfterSeconds); }
            RenderCards();
            if(value.status!="retry"&&Model.SelectedPatientId==value.patientId)Select(value.patientId);
            else RenderDetail();
        }
        void Failed(string route,ErrorResponse error)
        {
            var id=route.Split('/').ElementAtOrDefault(2);
            if(route.EndsWith("/brief",StringComparison.Ordinal)) Model.FailDetail(Uri.UnescapeDataString(id??""),error?.error?.message);
            else notice=error?.error?.message??"Request failed. Refresh to retry.";
            retrying=""; RenderDetail();
        }
        public void Filter(string procedure="",string urgency="") { Model.SetFilters(procedure,urgency);RenderFilters();RenderCards();RenderDetail(); }
        void RenderFilters()
        {
            ShellView.Clear(filters);
            string[] labels={"All","Appendix","Gallbladder","Colon"}; string[] ids={"","lap_appendectomy","lap_cholecystectomy","lap_sigmoid_colectomy"};
            for(int i=0;i<labels.Length;i++) { string id=ids[i]; ShellView.Button(filters,(Model.ProcedureFilter==id?"• ":"")+labels[i],new Vector3(-.348f+i*.232f,.305f,-.006f),new Vector2(.22f,.042f),()=>Filter(id,Model.UrgencyFilter)); }
            string[] urgencies={"","emergency","urgent","elective"};
            for(int i=0;i<urgencies.Length;i++) { string id=urgencies[i]; ShellView.Button(filters,(Model.UrgencyFilter==id?"• ":"")+(id==""?"Any urgency":id),new Vector3(-.348f+i*.232f,.248f,-.006f),new Vector2(.22f,.042f),()=>Filter(Model.ProcedureFilter,id)); }
        }
        void RenderCards()
        {
            RefreshBanner(); ShellView.Clear(cards); var rows=Model.VisiblePatients();
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i]; int column=i%4, line=i/4;
                // Extra sandbox rows remain visible if supplied; the demo catalog occupies exactly 4 x 3.
                var card=ShellView.Button(cards,"",new Vector3(-.348f+column*.232f,.13f-line*.172f,0),new Vector2(.22f,.16f),()=>Select(row.patientId),ExplorePatientModel.CanSelect(row));
                card.name="Patient_"+row.scenarioId;card.lift=true;
                var c=card.transform;
                CardLabel(c,Short(Model.Name(row),25),-.098f,.072f,.024f,.196f,.024f);
                CardLabel(c,string.IsNullOrEmpty(row.patientId)?"Connection scenario":Model.Demographics(row),-.098f,.043f,.018f,.196f,.019f);
                CardLabel(c,Short(Model.Complaint(row),29),-.098f,.020f,.019f,.196f,.019f);
                CardLabel(c,ProcedureLabel(row.procedureId)+" · "+(row.urgency??""),-.098f,-.005f,.018f,.196f,.019f);
                string status=ExplorePatientModel.StatusLabel(row.status);
                if(string.IsNullOrEmpty(row.patientId))status="No patient record";
                if(row.status=="blocked")status=Model.StatusReason(row).ToLowerInvariant().Contains("revoked")?"Consent revoked":Short(Model.StatusReason(row),25);
                Icon(c,row.status,new Vector3(-.09f,-.038f,-.01f));
                CardLabel(c,status,-.07f,-.030f,.018f,.166f,.017f);
                if(!ExplorePatientModel.HasNativeEncounter(row.patientId)&&ExplorePatientModel.CanSelect(row)) CardLabel(c,"Interview coming soon",-.098f,-.053f,.015f,.196f,.017f);
            }
            if(summary)summary.text=rows.Length==0?"No matching patients. Change a filter.":rows.Length+" records · select, review, then begin";
        }
        static string Excerpt(string value,int width,int count)
        {
            var lines=EncounterOfficePanel.Wrap(value,width).Split('\n');
            if(lines.Length<=count)return string.Join("\n",lines);
            var shown=lines.Take(count).ToArray();shown[count-1]=Short(shown[count-1],width-1).TrimEnd('…')+"…";return string.Join("\n",shown);
        }
        static void CardLabel(Transform parent,string value,float x,float top,float preferredHeight,float width,float maximumHeight)
        {
            var text=ShellView.Text(parent,value,new Vector3(x,top,-.009f),preferredHeight,width);
            var fit=text.GetComponent<EncounterOfficeText>();fit.maximumHeight=maximumHeight;fit.Fit();
            // Inter's generated glyph bounds need not begin at TextMesh's anchor. Align the
            // measured top to its authored row, and cap height so adjacent rows cannot overlap.
            var position=text.transform.localPosition;position.y-=text.GetComponent<Renderer>().localBounds.max.y;
            text.transform.localPosition=position;
        }
        static string Short(string text,int max) => string.IsNullOrEmpty(text)?"Unavailable":text.Length>max?text.Substring(0,max-1)+"…":text;
        static string ProcedureLabel(string id) => id=="lap_appendectomy"?"Appendix":id=="lap_cholecystectomy"?"Gallbladder":id=="lap_sigmoid_colectomy"?"Colon":"No procedure";
        static void Icon(Transform parent,string status,Vector3 at)
        {
            // Geometry icons avoid missing glyphs in Inter: check, flag, lock, or retry arrow.
            var path=new GameObject("StatusIcon_"+status).AddComponent<LineRenderer>();path.transform.SetParent(parent,false);path.transform.localPosition=at;
            path.useWorldSpace=false;path.startWidth=path.endWidth=.0018f;path.sharedMaterial=ShellView.Accent;
            Vector3[] points=status=="ready"?new[]{new Vector3(-.006f,0,0),new Vector3(-.001f,-.005f,0),new Vector3(.007f,.005f,0)}:
                status=="needs_review"?new[]{new Vector3(-.006f,-.007f,0),new Vector3(-.006f,.007f,0),new Vector3(.006f,.003f,0),new Vector3(-.006f,0,0)}:
                status=="retry"?new[]{new Vector3(.005f,-.006f,0),new Vector3(-.005f,-.006f,0),new Vector3(-.007f,.003f,0),new Vector3(0,.008f,0),new Vector3(.007f,.002f,0),new Vector3(.001f,.002f,0)}:
                new[]{new Vector3(-.007f,-.006f,0),new Vector3(.007f,-.006f,0),new Vector3(.007f,.002f,0),new Vector3(-.007f,.002f,0),new Vector3(-.007f,-.006f,0),new Vector3(-.004f,.002f,0),new Vector3(-.004f,.008f,0),new Vector3(.004f,.008f,0),new Vector3(.004f,.002f,0)};
            path.positionCount=points.Length;path.SetPositions(points);
        }
        public bool Select(string patientId)
        {
            if(Transitioning)return false;
            if(!Model.Select(patientId)){RenderDetail();return false;}
            RenderDetail(); service.LoadBrief(patientId);return true;
        }
        void RenderDetail()
        {
            RefreshBanner();ShellView.Clear(detail);BeginButton=null;retryButton=null;
            var selected=Model.Selected;if(selected==null)return;
            var panel=ShellView.Panel(detail,"SelectedChart",new Vector3(.79f,-.035f,-.025f),new Vector2(.59f,.72f));
            ShellView.Text(panel,Model.Name(selected),new Vector3(-.263f,.325f,-.008f),.037f,.526f);
            ShellView.Text(panel,Model.Demographics(selected),new Vector3(-.263f,.258f,-.008f),.029f,.526f);
            ShellView.Text(panel,Excerpt(Model.Complaint(selected),35,2),new Vector3(-.263f,.205f,-.008f),.028f,.526f);
            string chart=Model.DetailLoading?"Loading chart…":Model.DetailError;
            if(Model.SelectedBrief!=null)
            {
                var brief=Model.SelectedBrief;
                chart=string.Join("\n",(brief.chart??Array.Empty<ChartLine>()).Where(c=>!string.Equals(c.section,"Patient",StringComparison.OrdinalIgnoreCase)).Take(3).Select(c=>c.section+": "+c.text));
                if(brief.dataGaps?.Length>0)chart+="\nChart has gaps: "+brief.dataGaps[0].message;
                chart=Excerpt("Chart highlights\n"+chart,36,7);
            }
            ShellView.Text(panel,chart??"",new Vector3(-.263f,.105f,-.008f),.027f,.526f);
            string reason=Model.AvailabilityReason;
            if(Model.CanBegin && !CanBegin)reason="Offline chart · reconnect to begin";
            ShellView.Text(panel,EncounterOfficePanel.Wrap(reason,38),new Vector3(-.263f,-.18f,-.008f),.026f,.526f);
            if(selected.status=="retry"||!string.IsNullOrEmpty(Model.DetailError))
                retryButton=ShellView.Button(panel,"Try again",new Vector3(0,-.265f,-.012f),new Vector2(.49f,.07f),Retry);
            else BeginButton=ShellView.Button(panel,"Begin encounter",new Vector3(0,-.265f,-.012f),new Vector2(.49f,.07f),()=>Begin(),CanBegin);
        }
        void Retry()
        {
            if(retrying.Length>0||Time.unscaledTime<retryAt||Model.Selected==null)return;
            retrying=Model.SelectedPatientId;notice="Retrying chart…";RenderDetail();
            if(Model.Selected.status=="retry")service.LoadCase(retrying);else {retrying="";Select(Model.SelectedPatientId);}
        }
        public bool Begin()
        {
            if(!CanBegin||Transitioning)return false;
            var row=Model.Selected;string title="Office · "+Model.Name(row)+", "+(Model.SelectedBrief.patient?.age.ToString()??"age unknown")+"\n"+Short(Model.Complaint(row),90);
            Transitioning=ShellTransition.Ensure().BeginOffice(row.patientId,title,service.BaseUrl);
            if(!Transitioning) { notice=ShellTransition.LastError??"Unable to begin. Refresh and try again.";RenderDetail(); }
            return Transitioning;
        }
    }
}
