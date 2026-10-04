using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Brand;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Preop;
using TMPro;
using UnityEngine;

namespace Scalpal.Shell
{
    public sealed class HubController : MonoBehaviour
    {
        public ScalpalBrand brand;
        public Transform content;
        public ShellInput input;
        public ScalpalPreopService service;
        public ExplorePatientModel Model { get; private set; } = new ExplorePatientModel();
        public bool Exploring { get; private set; }
        public bool Transitioning { get; private set; }
        public ShellButton BeginButton { get; private set; }
        Transform launch, explore, cards, detail, filters, paging;
        public int Page { get; private set; }
        public int PageCount => Mathf.Max(1,Mathf.CeilToInt(Model.VisiblePatients().Length/12f));
        TextMeshPro banner, summary;
        ShellButton retryButton;
        string notice = "Loading patient records…", retrying = "";
        readonly Dictionary<string,float> retryDeadlines = new Dictionary<string,float>();
        string liveBriefId = "", renderedBanner, renderedRetry;
        int renderedSeconds=-1;string renderedRetrying;
        ShellTransition transition;
        public float RetryRemaining => Mathf.Max(0, retryDeadlines.TryGetValue(Model.SelectedPatientId,out var until) ? until-Time.unscaledTime : 0);
        bool initialized, wired, offlineSeen;
        public bool CanBegin => Model.CanBegin && liveBriefId == Model.SelectedPatientId && service;
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
            ShellView.Configure(brand ? brand : ScalpalBrand.Active);
            // Site lockup and nothing else: dither mark, the "Scalpal." wordmark in Instrument Serif, one Start.
            launch = ShellView.Panel(content, "Launch", new Vector3(0, 0, 1.3f), new Vector2(.62f,.46f));
            ShellView.Brand.Mark(launch, new Vector3(0,.135f,-.008f), .085f);
            var wordmark = ShellView.Text(launch, Wordmark, new Vector3(0,.03f,-.008f), .074f,.56f,TextAnchor.MiddleCenter,ScalpalTextRole.Wordmark);
            wordmark.name = "Wordmark";
            ShellView.Button(launch, "Start", new Vector3(0,-.12f,-.008f),new Vector2(.32f,.080f),Enter,true,true);
            explore = new GameObject("Explore").transform; explore.SetParent(content,false); explore.localPosition = new Vector3(0,0,1.3f);
            ShellView.Panel(explore,"ExploreGlass",new Vector3(0,-.03f,.012f),new Vector2(2.22f,1.60f));
            ShellView.Text(explore,"Explore patients",new Vector3(-1.04f,.715f,-.01f),.05f,1.0f,TextAnchor.UpperLeft,ScalpalTextRole.Title);
            filters = Child(explore,"Filters"); cards = Child(explore,"PatientCards"); detail = Child(explore,"PatientDetail");paging=Child(explore,"Paging");
            banner = ShellView.Text(explore,"",new Vector3(-1.04f,-.668f,-.01f),.026f,1.70f);
            summary = ShellView.Text(explore,"",new Vector3(-1.04f,-.602f,-.01f),.026f,1.72f);
            ShellView.Button(explore,"Refresh",new Vector3(.91f,-.74f,-.012f),new Vector2(.24f,.07f),Reload);
            Readable(launch);Readable(explore);
            RenderFilters(); RenderCards(); RenderDetail(); explore.gameObject.SetActive(false);
        }
        public const string Wordmark = "Scalpal.";
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
            service.RequestRetryAfterForRoute += RetryAfter;
            transition=ShellTransition.Ensure();transition.Finished += TransitionFinished;
            service.CaseLoaded += CaseLoaded; service.RequestFailedForRoute += Failed;service.OfflineModeChanged += OfflineChanged;
        }
        void OnDestroy()
        {
            if(!wired || !service) return;
            service.BundleLoaded -= BundleLoaded; service.PatientsLoaded -= PatientsLoaded; service.BriefLoaded -= BriefLoaded;
            service.RequestRetryAfterForRoute -= RetryAfter;
            if(transition)transition.Finished -= TransitionFinished;
            service.CaseLoaded -= CaseLoaded; service.RequestFailedForRoute -= Failed;service.OfflineModeChanged -= OfflineChanged;
        }
        void Update()
        {
            if(retryButton)
            {
                int seconds=Mathf.CeilToInt(RetryRemaining);
                retryButton.interactable=retrying.Length==0&&seconds==0;
                if(renderedSeconds==seconds && renderedRetrying==retrying && renderedRetry!=null)return;
                renderedSeconds=seconds;renderedRetrying=retrying;
                string label=retrying.Length>0?"Retrying…":seconds>0?"Try again in "+seconds+"s":"Try again";
                if(renderedRetry!=label) { renderedRetry=label;ShellView.SetText(retryButton.label,label); }
            }
        }
        void RefreshBanner()
        {
            string value=notice;
            if(renderedBanner==value)return;renderedBanner=value;ShellView.SetText(banner,value);
        }
        void OfflineChanged(bool value) { offlineSeen=value;RefreshBanner(); }
        void TransitionFinished(bool success,string error)
        {
            Transitioning=false;
            if(!success) { notice=error??"Unable to load office. Try again.";RenderDetail(); }
        }
        void RetryAfter(string route,int seconds)
        {
            string id=Uri.UnescapeDataString(route.Split('/').ElementAtOrDefault(2)??"");
            if(id.Length>0)retryDeadlines[id]=Time.unscaledTime+Mathf.Max(1,seconds);
        }
        public void Enter() { Initialize(); Exploring=true; launch.gameObject.SetActive(false); explore.gameObject.SetActive(true); }
        public void Reload()
        {
            if(!service || Transitioning) return;
            Wire(); service.CancelPendingRequests(); retrying=""; liveBriefId=""; Model.ClearSelection();
            notice="Loading patient records…"; RenderDetail(); service.LoadPatients();
        }
        public void BundleLoaded(ScalpalBundle bundle) { Model.ApplyBundle(bundle); RenderCards(); RenderDetail(); }
        public void PatientsLoaded(PatientList list)
        {
            offlineSeen=service.LastResponseOffline;Model.ApplyPatients(list);
            notice=list?.patients==null?"Patient list unavailable. Refresh to retry.":"Select a chart to explore.";
            // Live list comes first. The local bundle only fills metadata missing from this DTO.
            service.LoadCachedBundle();RenderCards();RenderDetail();
        }
        public void BriefLoaded(PreopBrief brief)
        {
            if(!Model.ApplyBrief(brief))return;
            liveBriefId=service.LastResponseOffline?"":brief.patientId;
            if(!service.LastResponseOffline)offlineSeen=false;
            RenderDetail();
        }
        void CaseLoaded(SurgicalCase value)
        {
            if(value==null) return; Model.ApplyCase(value);
            if(retrying==value.patientId) { retrying="";if(value.status=="retry")retryDeadlines[value.patientId]=Time.unscaledTime+Mathf.Max(1,value.retryAfterSeconds);else retryDeadlines.Remove(value.patientId); }
            RenderCards();
            if(value.status!="retry"&&Model.SelectedPatientId==value.patientId)Select(value.patientId);
            else RenderDetail();
        }
        void Failed(string route,ErrorResponse error)
        {
            var id=route.Split('/').ElementAtOrDefault(2);
            if(route.EndsWith("/brief",StringComparison.Ordinal)) Model.FailDetail(Uri.UnescapeDataString(id??""),error?.error?.message);
            else notice=error?.error?.message??"Request failed. Refresh to retry.";
            if(Uri.UnescapeDataString(id??"")==retrying)retrying=""; RenderDetail();
        }
        public void Filter(string procedure="",string urgency="") { Page=0;Model.SetFilters(procedure,urgency);RenderFilters();RenderCards();RenderDetail(); }
        void RenderFilters()
        {
            ShellView.Clear(filters);
            string[] labels={"All","Appendix","Gallbladder","Colon"}; string[] ids={"","appendectomy","lap_cholecystectomy","lap_sigmoid_colectomy"};
            // Chips size to their label so mono labels keep the 32 mm/m floor; the active chip carries the accent.
            float x=-1.06f;
            for(int i=0;i<labels.Length;i++) { string id=ids[i]; x=Chip(labels[i],x,Model.ProcedureFilter==id,()=>Filter(id,Model.UrgencyFilter)); }
            string[] urgencies={"","emergency","urgent","elective"}; string[] urgencyLabels={"Any","Emergency","Urgent","Elective"};
            x+=.09f;
            for(int i=0;i<urgencies.Length;i++) { string id=urgencies[i]; x=Chip(urgencyLabels[i],x,Model.UrgencyFilter==id,()=>Filter(Model.ProcedureFilter,id)); }
            Readable(filters);
        }
        float Chip(string label,float left,bool active,Action action)
        {
            float width=label.Length*.0262f+.055f;
            ShellView.Button(filters,label,new Vector3(left+width/2,.565f,-.006f),new Vector2(width,.056f),action,true,active);
            return left+width+.016f;
        }
        // Raise every string under root to its brand floor for its distance from the hub viewer (content origin + 8 cm).
        void Readable(Transform root) { if(content) ScalpalBrandLayout.SizeForViewer(root,content.TransformPoint(Vector3.up*.08f)); }
        public void SetPage(int page)
        {
            Page=Mathf.Clamp(page,0,PageCount-1);Model.ClearSelection();liveBriefId="";RenderCards();RenderDetail();
        }
        void RenderCards()
        {
            RefreshBanner();ShellView.Clear(cards);ShellView.Clear(paging);
            var all=Model.VisiblePatients();Page=Mathf.Clamp(Page,0,PageCount-1);var rows=all.Skip(Page*12).Take(12).ToArray();
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];int column=i%4,line=i/4;
                var card=ShellView.Button(cards,"",new Vector3(-.795f+column*.53f,.26f-line*.34f,0),new Vector2(.50f,.32f),()=>Select(row.patientId),ExplorePatientModel.CanSelect(row));
                card.name="Patient_"+row.scenarioId;card.lift=true;var c=card.transform;
                ShellView.FixedText(c,Model.Name(row),new Vector3(-.228f,.148f,-.009f),.060f,.456f,ScalpalTextRole.Title);
                ShellView.FixedText(c,Model.Demographics(row),new Vector3(-.228f,.075f,-.009f),.043f,.456f);
                ShellView.FixedText(c,Model.Complaint(row),new Vector3(-.228f,.019f,-.009f),.043f,.456f);
                ShellView.FixedText(c,ProcedureLabel(row.procedureId)+" · "+(row.urgency??""),new Vector3(-.228f,-.037f,-.009f),.043f,.456f);
                Icon(c,row.status,new Vector3(-.215f,-.113f,-.01f));
                ShellView.FixedText(c,ExplorePatientModel.StatusLabel(row.status),new Vector3(-.190f,-.093f,-.009f),.043f,.418f);
            }
            string count=all.Length==0?"No matching patients":all.Length+" patients";
            ShellView.SetText(summary,count);
            if(PageCount>1)
            {
                ShellView.Button(paging,"Previous",new Vector3(.62f,-.645f,-.014f),new Vector2(.27f,.065f),()=>SetPage(Page-1),Page>0);
                ShellView.Button(paging,"Next",new Vector3(.91f,-.645f,-.014f),new Vector2(.27f,.065f),()=>SetPage(Page+1),Page<PageCount-1);
                Readable(paging);
                ShellView.SetText(summary,all.Length+" patients · page "+(Page+1)+" / "+PageCount);
            }
        }
        static string Excerpt(string value,int width,int count)
        {
            var lines=EncounterOfficePanel.Wrap(value,width).Split('\n');
            if(lines.Length<=count)return string.Join("\n",lines);
            var shown=lines.Take(count).ToArray();shown[count-1]=Short(shown[count-1],width-1).TrimEnd('…')+"…";return string.Join("\n",shown);
        }
        static string Short(string text,int max) => string.IsNullOrEmpty(text)?"Unavailable":text.Length>max?text.Substring(0,max-1)+"…":text;
        static string ProcedureLabel(string id) => (id=="lap_appendectomy" || id=="open_appendectomy")?"Appendix":id=="lap_cholecystectomy"?"Gallbladder":id=="lap_sigmoid_colectomy"?"Colon":"No procedure";
        static void Icon(Transform parent,string status,Vector3 at)
        {
            // Geometry icons, drawn in the brand accent: check, flag, lock, or retry arrow.
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
            liveBriefId="";
            if(!Model.Select(patientId)){RenderDetail();return false;}
            RenderDetail(); if(Model.Selected.status!="retry")service.LoadBrief(patientId);return true;
        }
        void RenderDetail()
        {
            RefreshBanner();ShellView.Clear(detail);BeginButton=null;retryButton=null;renderedRetry=null;
            var selected=Model.Selected;if(selected==null)return;
            var panel=ShellView.Panel(detail,"SelectedChart",new Vector3(1.472f,-.035f,-.45f),new Vector2(.70f,.72f));
            ShellView.Text(panel,Model.Name(selected),new Vector3(-.263f,.325f,-.008f),.037f,.526f,TextAnchor.UpperLeft,ScalpalTextRole.Title);
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
            ShellView.Text(panel,EncounterOfficePanel.Wrap(reason,38),new Vector3(-.263f,-.16f,-.008f),.026f,.526f);
            if(selected.status=="retry"||!string.IsNullOrEmpty(Model.DetailError))
                retryButton=ShellView.Button(panel,"Try again",new Vector3(0,-.265f,-.012f),new Vector2(.49f,.07f),Retry,retrying.Length==0&&RetryRemaining<=0);
            else BeginButton=ShellView.Button(panel,"Begin encounter",new Vector3(0,-.265f,-.012f),new Vector2(.49f,.07f),()=>Begin(),CanBegin,true);
            panel.localRotation=Quaternion.Euler(0,60,0);panel.localScale=Vector3.one*1.4f;
            foreach(var fit in panel.GetComponentsInChildren<ScalpalTextFit>()) { fit.maximumWidth*=1.4f;fit.maximumHeight*=1.4f;fit.Fit(); }
            Readable(panel);
        }
        public void Retry()
        {
            if(retrying.Length>0||RetryRemaining>0||Model.Selected==null)return;
            retrying=Model.SelectedPatientId;notice="Retrying chart…";RenderDetail();
            if(Model.Selected.status=="retry")service.LoadCase(retrying);else {retrying="";Select(Model.SelectedPatientId);}
        }
        public bool Begin()
        {
            if(!CanBegin||Transitioning)return false;
            var row=Model.Selected;string title="Office · "+Model.Name(row)+", "+(Model.SelectedBrief.patient != null && Model.SelectedBrief.patient.age>=0?Model.SelectedBrief.patient.age.ToString():"age unknown")+"\n"+Short(Model.Complaint(row),90);
            Transitioning=ShellTransition.Ensure().BeginOffice(row.patientId,title,service.BaseUrl);
            if(!Transitioning) { notice=ShellTransition.LastError??"Unable to begin. Refresh and try again.";RenderDetail(); }
            return Transitioning;
        }
    }
}
