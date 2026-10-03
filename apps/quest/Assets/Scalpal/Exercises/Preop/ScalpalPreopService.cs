// Scene-facing client for the scalpal-preop service. UI binds buttons to the ScalpalAction objects the
// service returns and calls Dispatch(action); every route kind has a handler, so no button is dead.
// When the service is unreachable it answers from Resources/scalpal_bundle.json instead.
using System;
using System.Collections;
using System.Linq;
using System.Text;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Exercises.Generated;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Exercises.Preop
{
    public class ScalpalPreopService : MonoBehaviour
    {
        [Tooltip("The Mac's LAN address when running on Quest, e.g. http://192.168.1.20:8787. localhost only works in the editor.")]
        [SerializeField] string baseUrl = "http://localhost:8787";
        [SerializeField] int timeoutSeconds = 8;
        [SerializeField] bool forceOffline;
        [SerializeField] string offlineBundleResource = "scalpal_bundle";

        ScalpalBundle offlineBundle;

        public bool IsOffline { get; private set; }

        public event Action<ServiceIndex> IndexLoaded;
        public event Action<PatientList> PatientsLoaded;
        public event Action<SurgicalCase> CaseLoaded;
        public event Action<PreopBrief> BriefLoaded;
        public event Action<PreopCheckResult> PreopChecked;
        public event Action<ProcedureList> ProceduresLoaded;
        public event Action<Procedure> ProcedureLoaded;
        public event Action<AnatomyList> AnatomyLoaded;
        public event Action<InstrumentList> InstrumentsLoaded;
        public event Action<ConnectResult> ConnectFinished;
        public event Action<ScalpalBundle> BundleLoaded;
        public event Action<ErrorResponse> RequestFailed;
        public event Action<bool> OfflineModeChanged;

        public void LoadPatients() => Dispatch(Get("/patients"));
        public void LoadCase(string patientId) => Dispatch(Get($"/patients/{patientId}/case"));

        public void SubmitPreopCheck(string patientId, string[] selected) =>
            Dispatch(new ScalpalAction { id = "submit_preop_check", method = "POST", route = $"/patients/{patientId}/preop-check" }, selected);

        // The single entry point for buttons. selected is only used by the pre-op check.
        public void Dispatch(ScalpalAction action, string[] selected = null)
        {
            if (action == null)
            {
                Fail("missing_action", "A button had no action bound.");
                return;
            }
            var kind = ScalpalRoutes.Resolve(action.method, action.route);
            if (kind == RouteKind.Unknown)
            {
                Fail("unknown_route", $"No handler for {action.method} {action.route}.");
                return;
            }
            var body = kind == RouteKind.PreopCheck ? JsonUtility.ToJson(new PreopCheckRequest { selected = selected ?? new string[0] }) : null;
            if (forceOffline) Answer(kind, action.route, null, selected);
            else StartCoroutine(Send(kind, action, body, selected));
        }

        IEnumerator Send(RouteKind kind, ScalpalAction action, string body, string[] selected)
        {
            using (var req = new UnityWebRequest(baseUrl.TrimEnd('/') + action.route, action.method))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.SetRequestHeader("Accept", "application/json");
                req.timeout = timeoutSeconds;
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.ConnectionError)
                {
                    SetOffline(true);
                    Answer(kind, action.route, null, selected);
                    yield break;
                }
                SetOffline(false);
                var json = req.downloadHandler.text;
                if (req.result == UnityWebRequest.Result.ProtocolError)
                {
                    var error = TryParse<ErrorResponse>(json);
                    RequestFailed?.Invoke(error?.error != null ? error : Error($"http_{req.responseCode}", $"HTTP {req.responseCode} from {action.route}"));
                    yield break;
                }
                Answer(kind, action.route, json, selected);
            }
        }

        // json == null means answer from the offline bundle.
        void Answer(RouteKind kind, string route, string json, string[] selected)
        {
            var bundle = json == null ? Bundle() : null;
            if (json == null && bundle == null)
            {
                Fail("offline_unavailable", "The service is unreachable and no offline bundle is packaged.");
                return;
            }
            var segment = route.Split('/').ElementAtOrDefault(2) ?? "";

            switch (kind)
            {
                case RouteKind.Index:
                case RouteKind.Health:
                    if (json != null) IndexLoaded?.Invoke(TryParse<ServiceIndex>(json));
                    else IndexLoaded?.Invoke(new ServiceIndex { service = "scalpal-preop (offline)", description = "", disclaimer = bundle.disclaimer, actions = new[] { Get("/patients") } });
                    break;
                case RouteKind.Patients:
                    PatientsLoaded?.Invoke(json != null ? TryParse<PatientList>(json) : new PatientList { patients = bundle.patients, actions = new[] { Get("/") } });
                    break;
                case RouteKind.Case:
                    var kase = json != null ? TryParse<SurgicalCase>(json) : FindOfflineCase(segment);
                    if (kase != null) CaseLoaded?.Invoke(kase);
                    else Fail("patient_not_found", "That patient is not in the offline bundle.");
                    break;
                case RouteKind.Brief:
                    var brief = json != null ? TryParse<PreopBrief>(json) : FindOfflineCase(segment)?.brief;
                    if (brief != null) BriefLoaded?.Invoke(brief);
                    else Fail("patient_not_found", "That patient is not in the offline bundle.");
                    break;
                case RouteKind.PreopCheck:
                    if (json != null) PreopChecked?.Invoke(TryParse<PreopCheckResult>(json));
                    else
                    {
                        var offlineCase = FindOfflineCase(segment);
                        if (offlineCase != null && !string.IsNullOrEmpty(offlineCase.procedureId)) PreopChecked?.Invoke(PreopScorer.Score(offlineCase, selected));
                        else Fail("case_unavailable", "That case cannot be checked offline.");
                    }
                    break;
                case RouteKind.Procedures:
                    ProceduresLoaded?.Invoke(json != null ? TryParse<ProcedureList>(json) : new ProcedureList
                    {
                        procedures = bundle.procedures.Select(p => new ProcedureSummary { id = p.id, title = p.title, shortTitle = p.shortTitle, summary = p.summary, stepCount = p.steps.Length, actions = new[] { Get($"/procedures/{p.id}") } }).ToArray(),
                        actions = new[] { Get("/") },
                    });
                    break;
                case RouteKind.Procedure:
                    var procedure = json != null ? TryParse<Procedure>(json) : bundle.procedures.FirstOrDefault(p => p.id == segment);
                    if (procedure != null) ProcedureLoaded?.Invoke(procedure);
                    else Fail("procedure_not_found", "Unknown procedure.");
                    break;
                case RouteKind.Anatomy:
                    AnatomyLoaded?.Invoke(json != null ? TryParse<AnatomyList>(json) : new AnatomyList { structures = bundle.anatomy, actions = new[] { Get("/") } });
                    break;
                case RouteKind.Instruments:
                    InstrumentsLoaded?.Invoke(json != null ? TryParse<InstrumentList>(json) : new InstrumentList { instruments = bundle.instruments, actions = new[] { Get("/") } });
                    break;
                case RouteKind.Connect:
                    ConnectFinished?.Invoke(json != null ? TryParse<ConnectResult>(json) : new ConnectResult
                    {
                        sessionId = "",
                        scenarioId = segment,
                        status = "failed",
                        failureCode = "offline",
                        failureMessage = "Health system connections need the network.",
                        patientId = "",
                        say = "I'm offline, so I can't connect a new health system. Pick a patient from the list.",
                        actions = new[] { Get("/patients") },
                    });
                    break;
                case RouteKind.Bundle:
                    BundleLoaded?.Invoke(json != null ? TryParse<ScalpalBundle>(json) : bundle);
                    break;
            }
        }

        SurgicalCase FindOfflineCase(string idOrScenario)
        {
            var bundle = Bundle();
            return bundle?.cases.FirstOrDefault(c => c.patientId == idOrScenario || c.scenarioId == idOrScenario);
        }

        ScalpalBundle Bundle()
        {
            if (offlineBundle != null) return offlineBundle;
            var asset = Resources.Load<TextAsset>(offlineBundleResource);
            if (asset != null) offlineBundle = JsonUtility.FromJson<ScalpalBundle>(asset.text);
            return offlineBundle;
        }

        void SetOffline(bool offline)
        {
            if (IsOffline == offline) return;
            IsOffline = offline;
            OfflineModeChanged?.Invoke(offline);
        }

        void Fail(string code, string message) => RequestFailed?.Invoke(Error(code, message));

        static ErrorResponse Error(string code, string message) =>
            new ErrorResponse { error = new ErrorInfo { code = code, message = message }, actions = new[] { Get("/patients") } };

        static ScalpalAction Get(string route) => new ScalpalAction { id = "navigate", label = "", method = "GET", route = route };

        static T TryParse<T>(string json) where T : class
        {
            try
            {
                return JsonUtility.FromJson<T>(json);
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"[Scalpal] Could not parse {typeof(T).Name}: {e.Message}");
                return null;
            }
        }
    }
}
