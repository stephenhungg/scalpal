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
        public ShellButton SkipButton { get; private set; }
        Transform launch, explore, frame, cards, detail, paging;
        public int Page { get; private set; }
        // Three columns, at most three rows: fewer, larger targets with generous gutters.
        public const int Columns = 3, Rows = 3, PageSize = Columns * Rows;
        public const float CardWidth = .50f, CardHeight = .21f, ColumnGap = .06f, RowGap = .06f;
        const float SideMargin = .13f, TitleBand = .23f, BottomMargin = .13f, PagingBand = .11f;
        // Detail panel sits to the right of the grid, facing the viewer at this yaw and distance.
        public const float DetailYaw = 54, DetailDistance = 1.45f, DetailWidth = .76f;
        public int PageCount => Mathf.Max(1,Mathf.CeilToInt(Model.VisiblePatients().Length/(float)PageSize));
        // Shown only when a Begin or office load fails; cleared by the next selection.
        string beginError = "";
        readonly Dictionary<string,float> retryDeadlines = new Dictionary<string,float>();
        readonly HashSet<string> retryInFlight = new HashSet<string>();
        float catalogDeadline = -1;
        bool catalogInFlight;
        public const float DefaultRetrySeconds = 10, BriefRetrySeconds = 5;
        string liveBriefId = "";
        ShellTransition transition;
        public float RetryRemaining => Remaining(Model.SelectedPatientId);
        public float Remaining(string patientId) => Mathf.Max(0, patientId!=null && retryDeadlines.TryGetValue(patientId,out var until) ? until-Time.unscaledTime : 0);
        bool initialized, wired;
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
            // Explore is the title "Patients" and the cards. No filters, counts, hints or Refresh.
            frame = Child(explore,"ExploreFrame"); cards = Child(explore,"PatientCards"); detail = Child(explore,"PatientDetail");paging=Child(explore,"Paging");
            Readable(launch);
            RenderCards(); RenderDetail(); explore.gameObject.SetActive(false);
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
            catch(Exception error) { Debug.LogWarning("Scalpal shell: session-config.json is invalid; using the default endpoint. "+error.Message); }
        }
        void Wire()
        {
            if(wired) return; wired=true;
            service.BundleLoaded += BundleLoaded; service.PatientsLoaded += PatientsLoaded; service.BriefLoaded += BriefLoaded;
            service.RequestRetryAfterForRoute += RetryAfter;
            transition=ShellTransition.Ensure();transition.Finished += TransitionFinished;
            service.CaseLoaded += CaseLoaded; service.RequestFailedForRoute += Failed;
        }
        void OnDestroy()
        {
            if(!wired || !service) return;
            service.BundleLoaded -= BundleLoaded; service.PatientsLoaded -= PatientsLoaded; service.BriefLoaded -= BriefLoaded;
            service.RequestRetryAfterForRoute -= RetryAfter;
            if(transition)transition.Finished -= TransitionFinished;
            service.CaseLoaded -= CaseLoaded; service.RequestFailedForRoute -= Failed;
        }
        void Update() { if(Exploring) ServiceRetries(); }
        // Background recovery in place of a Refresh button. Every retry waits for the service's
        // Retry-After (or a fixed backoff) and only one request per target is ever in flight.
        public void ServiceRetries()
        {
            if(!service || Transitioning) return;
            float now=Time.unscaledTime;
            if(catalogDeadline>=0 && now>=catalogDeadline && !catalogInFlight) { catalogDeadline=-1;catalogInFlight=true;service.LoadPatients(); }
            foreach(var row in Model.Patients.Where(Model.Recoverable).ToArray())
            {
                if(retryInFlight.Contains(row.patientId) || Remaining(row.patientId)>0) continue;
                retryInFlight.Add(row.patientId);service.LoadCase(row.patientId);
            }
            var selected=Model.Selected;
            if(selected==null || selected.status=="retry" || Model.DetailLoading || retryInFlight.Contains(selected.patientId) || RetryRemaining>0) return;
            // A failed chart, or an offline one once the service answers live again, is re-requested.
            if(Model.DetailError.Length>0 && retryDeadlines.ContainsKey(selected.patientId) || Model.SelectedBrief!=null && liveBriefId!=selected.patientId && !service.IsOffline)
                Select(selected.patientId);
        }
        void Defer(string patientId,float seconds)
        {
            float until=Time.unscaledTime+Mathf.Max(1,seconds);
            if(!retryDeadlines.TryGetValue(patientId,out var current) || current<until) retryDeadlines[patientId]=until;
        }
        void TransitionFinished(bool success,string error)
        {
            Transitioning=false;
            if(!success) { beginError=error??"Unable to load the office.";RenderDetail(); }
        }
        void RetryAfter(string route,int seconds)
        {
            string id=Uri.UnescapeDataString(route.Split('/').ElementAtOrDefault(2)??"");
            if(id.Length>0)retryDeadlines[id]=Time.unscaledTime+Mathf.Max(1,seconds);
            else if(route=="/patients")catalogDeadline=Time.unscaledTime+Mathf.Max(1,seconds);
        }
        public void Enter() { Initialize(); Exploring=true; launch.gameObject.SetActive(false); explore.gameObject.SetActive(true); }
        // Back to the launch screen (Scalpal. + Start) from the explore grid.
        public void Home() { Initialize(); Exploring=false; explore.gameObject.SetActive(false); launch.gameObject.SetActive(true); }
        public void Reload()
        {
            if(!service || Transitioning) return;
            Wire(); service.CancelPendingRequests(); retryInFlight.Clear(); catalogInFlight=true; catalogDeadline=-1; liveBriefId=""; beginError=""; Model.ClearSelection();
            RenderDetail(); service.LoadPatients();
        }
        public void BundleLoaded(ScalpalBundle bundle) { Model.ApplyBundle(bundle); RenderCards(); RenderDetail(); }
        public void PatientsLoaded(PatientList list)
        {
            catalogInFlight=false;
            Model.ApplyPatients(list);
            // An offline catalog keeps probing for the live service in the background.
            if(service && service.LastResponseOffline && catalogDeadline<0) catalogDeadline=Time.unscaledTime+DefaultRetrySeconds;
            // Live list comes first. The local bundle only fills metadata missing from this DTO.
            if(service) service.LoadCachedBundle();
            RenderCards();RenderDetail();
        }
        public void BriefLoaded(PreopBrief brief)
        {
            if(!Model.ApplyBrief(brief))return;
            retryDeadlines.Remove(brief.patientId);
            liveBriefId=service.LastResponseOffline?"":brief.patientId;
            RenderDetail();
        }
        void CaseLoaded(SurgicalCase value)
        {
            if(value==null) return;
            retryInFlight.Remove(value.patientId);
            if(value.status=="retry")Defer(value.patientId,value.retryAfterSeconds);else retryDeadlines.Remove(value.patientId);
            bool wasSelected=Model.SelectedPatientId==value.patientId;
            Model.ApplyCase(value);
            RenderCards();
            if(value.status!="retry"&&wasSelected&&Model.SelectedPatientId==value.patientId)Select(value.patientId);
            else RenderDetail();
        }
        void Failed(string route,ErrorResponse error)
        {
            string id=Uri.UnescapeDataString(route.Split('/').ElementAtOrDefault(2)??"");
            if(route=="/patients") { catalogInFlight=false;if(catalogDeadline<Time.unscaledTime)catalogDeadline=Time.unscaledTime+DefaultRetrySeconds; }
            else if(route.EndsWith("/brief",StringComparison.Ordinal)) { Model.FailDetail(id,error?.error?.message);if(id.Length>0&&Remaining(id)<=0)Defer(id,BriefRetrySeconds); }
            else if(route.EndsWith("/case",StringComparison.Ordinal)) { retryInFlight.Remove(id);if(id.Length>0&&Remaining(id)<=0)Defer(id,DefaultRetrySeconds); }
            RenderDetail();
        }
        // Raise every string under root to its brand floor for its distance from the hub viewer (content origin + 8 cm).
        void Readable(Transform root) { if(content) ScalpalBrandLayout.SizeForViewer(root,content.TransformPoint(Vector3.up*.08f)); }
        public void SetPage(int page)
        {
            Page=Mathf.Clamp(page,0,PageCount-1);Model.ClearSelection();liveBriefId="";RenderCards();RenderDetail();
        }
        public static float GridWidth => Columns*CardWidth+(Columns-1)*ColumnGap;
        void RenderCards()
        {
            ShellView.Clear(frame);ShellView.Clear(cards);ShellView.Clear(paging);
            var all=Model.VisiblePatients();Page=Mathf.Clamp(Page,0,PageCount-1);var rows=all.Skip(Page*PageSize).Take(PageSize).ToArray();
            int lines=Mathf.Max(1,Mathf.CeilToInt(rows.Length/(float)Columns));
            float gridHeight=lines*CardHeight+(lines-1)*RowGap;
            float height=TitleBand+gridHeight+BottomMargin+(PageCount>1?PagingBand:0), width=GridWidth+2*SideMargin;
            float top=height/2, left=-GridWidth/2, gridTop=top-TitleBand;
            ShellView.Panel(frame,"ExploreGlass",new Vector3(0,0,.012f),new Vector2(width,height));
            var title=ShellView.Text(frame,"Patients",new Vector3(left,top-.085f,-.01f),.05f,GridWidth,TextAnchor.UpperLeft,ScalpalTextRole.Title);
            title.name="Title";
            Readable(frame);
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];int column=i%Columns,line=i/Columns;
                // A short last row is centred under the full rows.
                int inRow=Mathf.Min(Columns,rows.Length-line*Columns);
                float x=left+CardWidth/2+(column+(Columns-inRow)*.5f)*(CardWidth+ColumnGap);
                float y=gridTop-CardHeight/2-line*(CardHeight+RowGap);
                var card=ShellView.Button(cards,"",new Vector3(x,y,0),new Vector2(CardWidth,CardHeight),()=>Select(row.patientId),true);
                card.name="Patient_"+row.scenarioId;card.lift=true;var c=card.transform;
                // Name and one quiet line. Everything else lives in the side panel.
                ShellView.FixedText(c,Model.Name(row),new Vector3(-.21f,.0625f,-.009f),.060f,.40f,ScalpalTextRole.Title).name="Name";
                var meta=ShellView.FixedText(c,Model.CardLine(row),new Vector3(-.21f,-.0195f,-.009f),.043f,.40f);
                meta.name="Line";meta.color=new Color(1,1,1,.6f);
                if(row.status=="retry")RetryDot(c,new Vector3(.215f,.07f,-.01f));
            }
            if(PageCount>1)
            {
                float y=-top+BottomMargin*.5f+PagingBand*.5f;
                ShellView.Button(paging,"Previous",new Vector3(GridWidth/2-.42f,y,-.014f),new Vector2(.26f,.075f),()=>SetPage(Page-1),Page>0,false,true);
                ShellView.Button(paging,"Next",new Vector3(GridWidth/2-.13f,y,-.014f),new Vector2(.26f,.075f),()=>SetPage(Page+1),Page<PageCount-1,false,true);
                Readable(paging);
            }
        }
        static string Excerpt(string value,int width,int count)
        {
            var lines=EncounterOfficePanel.Wrap(value,width).Split('\n');
            if(lines.Length<=count)return string.Join("\n",lines);
            var shown=lines.Take(count).ToArray();shown[count-1]=Short(shown[count-1],width-1).TrimEnd('…')+"…";return string.Join("\n",shown);
        }
        static string Short(string text,int max) => string.IsNullOrEmpty(text)?"Unavailable":text.Length>max?text.Substring(0,max-1)+"…":text;
        // The only status mark on a card: a small neutral ring when the chart is still being retried.
        static void RetryDot(Transform parent,Vector3 at)
        {
            var ring=new GameObject("RetryDot").AddComponent<LineRenderer>();ring.transform.SetParent(parent,false);ring.transform.localPosition=at;
            ring.useWorldSpace=false;ring.loop=true;ring.startWidth=ring.endWidth=.004f;ring.sharedMaterial=ShellView.Accent;
            const int segments=16;ring.positionCount=segments;
            for(int i=0;i<segments;i++){float a=i*Mathf.PI*2/segments;ring.SetPosition(i,new Vector3(Mathf.Cos(a),Mathf.Sin(a),0)*.007f);}
        }
        public bool Select(string patientId)
        {
            if(Transitioning)return false;
            liveBriefId="";beginError="";
            if(!Model.Select(patientId)){RenderDetail();return false;}
            if(Model.Selected.status!="retry")service.LoadBrief(patientId);
            RenderDetail();return true;
        }
        // One short line, only when Begin cannot be pressed (or an attempt just failed).
        string Availability()
        {
            if(beginError.Length>0)return beginError;
            if(CanBegin)return "";
            var selected=Model.Selected;
            if(selected.status=="retry"||Model.DetailError.Length>0)return "Chart unavailable · retrying";
            if(Model.CanBegin)return "Offline · reconnect to begin";
            return Model.AvailabilityReason;
        }
        void RenderDetail()
        {
            ShellView.Clear(detail);BeginButton=null;SkipButton=null;
            var selected=Model.Selected;if(selected==null)return;
            var panel=Child(detail,"SelectedChart");
            float yaw=DetailYaw*Mathf.Deg2Rad;
            panel.localPosition=new Vector3(Mathf.Sin(yaw)*DetailDistance,0,Mathf.Cos(yaw)*DetailDistance-explore.localPosition.z);
            panel.localRotation=Quaternion.Euler(0,DetailYaw,0);
            const float pad=.06f, gap=.028f, sectionGap=.05f;
            float inner=DetailWidth-2*pad, left=-inner/2;
            var items=new List<(Transform item,float space)>();
            void Add(string value,ScalpalTextRole role,float space,string name)
            {
                if(string.IsNullOrWhiteSpace(value))return;
                var text=ShellView.Text(panel,value,new Vector3(left,0,-.008f),role==ScalpalTextRole.Title?.04f:.026f,inner,TextAnchor.UpperLeft,role);
                text.name=name;var fit=text.GetComponent<ScalpalTextFit>();fit.maximumHeight=value.Split('\n').Length*.08f;
                items.Add((text.transform,space));
            }
            Add(Model.Name(selected),ScalpalTextRole.Title,gap,"Name");
            Add(Model.AgeSex(selected),ScalpalTextRole.Body,sectionGap,"AgeSex");
            Add(Excerpt(Model.Presenting(selected),32,3),ScalpalTextRole.Body,gap,"Complaint");
            Add(Model.ProcedureAndUrgency(selected),ScalpalTextRole.Body,sectionGap,"Procedure");
            var highlights=Model.Highlights();
            Add(highlights.Length>0?string.Join("\n",highlights.Select(line=>Short(line,32))):Model.DetailLoading?"Loading chart…":"",ScalpalTextRole.Caption,sectionGap,"Highlights");
            Add(Availability(),ScalpalTextRole.Caption,gap,"Availability");
            Readable(panel);
            // Stack top-down with measured line extents so raising text to its floor can never overlap.
            float y=0;
            foreach(var (item,space) in items)
            {
                var fit=item.GetComponent<ScalpalTextFit>();fit.Fit();
                item.localPosition=new Vector3(left,y,-.008f);y-=fit.MeasuredSize().y+space;
            }
            const float buttonHeight=.085f, buttonGap=.02f;
            float beginWidth=.22f, skipWidth=inner-beginWidth-buttonGap, buttonY=y-buttonHeight/2;
            // Begin stays the primary action; Skip to surgery (ghost) goes to the Theatre card without the interview.
            BeginButton=ShellView.Button(panel,"Begin",new Vector3(left+beginWidth/2,buttonY,-.012f),new Vector2(beginWidth,buttonHeight),()=>Begin(),CanBegin,true);
            SkipButton=ShellView.Button(panel,"Skip to surgery",new Vector3(left+beginWidth+buttonGap+skipWidth/2,buttonY,-.012f),new Vector2(skipWidth,buttonHeight),()=>Begin(true),CanBegin,false,true);
            Readable(BeginButton.transform);Readable(SkipButton.transform);
            float contentHeight=-(buttonY-buttonHeight/2), height=contentHeight+2*pad;
            // Centre the stack vertically on the panel.
            foreach(Transform child in panel)child.localPosition+=Vector3.up*(contentHeight/2);
            ShellView.Panel(panel,"Glass",new Vector3(0,0,.004f),new Vector2(DetailWidth,height));
            DetailHalfHeight=height/2;
        }
        public float DetailHalfHeight { get; private set; }
        public bool Begin(bool skipToSurgery=false)
        {
            if(!CanBegin||Transitioning)return false;
            var row=Model.Selected;string title="Office · "+Model.Name(row)+", "+(Model.SelectedBrief.patient != null && Model.SelectedBrief.patient.age>=0?Model.SelectedBrief.patient.age.ToString():"age unknown")+"\n"+Short(Model.Complaint(row),90);
            if(skipToSurgery)title="Skip to surgery · "+Model.Name(row);
            Transitioning=ShellTransition.Ensure().BeginOffice(row.patientId,title,service.BaseUrl,skipToSurgery);
            if(!Transitioning) { beginError=ShellTransition.LastError??"Unable to begin. Try again.";RenderDetail(); }
            return Transitioning;
        }
    }
}
