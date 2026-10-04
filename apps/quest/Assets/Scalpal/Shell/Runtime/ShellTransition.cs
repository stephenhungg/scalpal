using System;
using System.Collections;
using System.Collections.Generic;
using Scalpal.Brand;
using Scalpal.EncounterOffice;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Scalpal.Shell
{
    // One-shot, explicit Explore -> Office contract. No encounter/attempt is invented in the shell.
    public sealed class ShellTransition : MonoBehaviour
    {
        public sealed class SelectedPatient
        {
            public readonly string patientId, serviceUrl;
            public readonly bool skipToSurgery; // "Skip to surgery": no interview, straight to the Theatre card
            public SelectedPatient(string id, string url, bool skip = false) { patientId=id; serviceUrl=url; skipToSurgery=skip; }
        }
        static ShellTransition instance;
        static SelectedPatient selected;
        public static bool Busy { get; private set; }
        public static string LastError { get; private set; }
        public event Action<bool,string> Finished;
        Material fadeMaterial;
        Transform fade, titleRoot;
        ShellInput transitionInput;
        EncounterOfficeRig gatedRig;
        bool rigWasEnabled;
        readonly List<Collider> gatedButtons = new List<Collider>();
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        NativeEncounterSession rigSession;
        bool RigReady => gatedRig && (gatedRig.Ready || DesktopPreview());
        static bool Paused => ShellPause.Instance && ShellPause.Instance.IsPaused;
        public static ShellTransition Ensure()
        {
            if (instance) return instance;
            return new GameObject("ScalpalSceneTransition").AddComponent<ShellTransition>();
        }
        void Awake()
        {
            if (instance && instance!=this) { Destroy(gameObject); return; }
            instance=this; DontDestroyOnLoad(gameObject);
            transitionInput=gameObject.AddComponent<ShellInput>(); transitionInput.enabled=false;
            Application.onBeforeRender += PositionFade;
        }
        void OnDestroy()
        {
            Application.onBeforeRender -= PositionFade;
            ClearTitle();
            if (fadeMaterial) Destroy(fadeMaterial);
            if (instance==this) { instance=null; Busy=false; selected=null; }
        }
        public static bool TryConsumeSelection(out SelectedPatient value)
        { value=selected; selected=null; return value!=null && EncounterContract.ValidPatientId(value.patientId); }
        public static bool TryStageSelection(string patientId,string serviceUrl,bool skipToSurgery=false)
        {
            if (Busy || !EncounterContract.ValidPatientId(patientId)) return false;
            if (!Uri.TryCreate(serviceUrl,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https")) return false;
            selected=new SelectedPatient(patientId,serviceUrl.TrimEnd('/'),skipToSurgery); return true;
        }
        public bool BeginOffice(string patientId,string title,string serviceUrl,bool skipToSurgery=false)
        {
            if (Busy || !EncounterContract.ValidPatientId(patientId)) return false;
            if (!Uri.TryCreate(serviceUrl,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https")) return false;
            if (!Application.CanStreamedLevelBeLoaded("DiagnosisOffice")) { LastError="Diagnosis office is missing from this build."; return false; }
            if (!TryStageSelection(patientId,serviceUrl,skipToSurgery)) return false;
            // This route deliberately starts after tracking/readiness, rather than from office Start.
            // Clear an abandoned legacy office selection so its Start cannot create a second encounter.
            EncounterOfficeRoute.TakePatient(out _,out _);
            StartCoroutine(Load("DiagnosisOffice",title,StartSelectedEncounter)); return true;
        }
        static void StartSelectedEncounter()
        {
            if (!TryConsumeSelection(out var handoff)) throw new InvalidOperationException("The selected patient handoff is missing or already consumed.");
            var office=FindFirstObjectByType<NativeEncounterSession>();
            if (!office) throw new InvalidOperationException("DiagnosisOffice has no NativeEncounterSession.");
            // Runs after Start's private endpoint config read, preserving the explicit Explore service choice.
            office.baseUrl=handoff.serviceUrl;
            if (office.SelectedPatientId==handoff.patientId && (office.Busy || office.State!=null)) return;
            office.StartPatient(handoff.patientId,handoff.skipToSurgery);
        }
        public IEnumerator Load(string scene,string title,Action afterLoad=null,float revealSeconds=.4f)
        {
            if (Busy) yield break;
            LastError=null;
            bool success=false, activated=false;
            Busy=true;
            try
            {
                if (!Application.CanStreamedLevelBeLoaded(scene)) { LastError="Scene unavailable: "+scene; yield break; }
                GateOfficeInput(); CreateFade(); yield return Fade(0,1,.4f);
                AsyncOperation operation=null;
                try { operation=SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single); }
                catch (Exception error) { LastError=error.Message; }
                if (operation==null) { LastError=LastError??("Scene loading did not start: "+scene); yield break; }
                operation.allowSceneActivation=false;
                while (operation.progress<.9f) yield return null;
                while (Paused) yield return null;
                operation.allowSceneActivation=true;
                while (!operation.isDone) yield return null;
                activated=true;
                // Wait for Start configuration, then require actual alignment before placing a title.
                yield return null;
                GateOfficeInput();
                if (scene=="DiagnosisOffice" && !gatedRig) { LastError="Office tracking rig is missing. Return to explore and retry."; yield break; }
                float waiting=0;
                while (gatedRig && (!RigReady || Paused))
                {
                    if (Paused) { yield return null; continue; }
                    transitionInput.driveHead=false;
                    gatedRig.enabled=true;
                    if (waiting>=2f) { LastError="Headset tracking is not ready. Return to explore and retry."; yield break; }
                    waiting+=Time.unscaledDeltaTime;
                    yield return null;
                }
                if (gatedRig)
                {
                    if (ShellPause.Instance) ShellPause.Instance.MarkOfficeAligned();
                    gatedRig.enabled=false; transitionInput.driveHead=true;
                }
                while (Paused) yield return null;
                try { afterLoad?.Invoke(); }
                catch (Exception exception) { LastError=exception.Message; }
                if (!string.IsNullOrEmpty(LastError)) yield break;
                PositionFade(); CreateTitle(title);
                float elapsed=0;
                while (elapsed<.8f) { if (!Paused) elapsed+=Time.unscaledDeltaTime; yield return null; }
                ClearTitle(); yield return Fade(1,0,Mathf.Clamp(revealSeconds,.1f,2f));
                success=true;
            }
            finally
            {
                selected=null; Busy=false;
                if (fade) fade.gameObject.SetActive(false);
                ClearTitle();
                if (transitionInput) transitionInput.enabled=false;
                if (gatedRig)
                {
                    gatedRig.session=rigSession;
                    if (ShellPause.Instance && ShellPause.Instance.IsPaused) ShellPause.Instance.RememberOfficeInput(rigWasEnabled);
                    else gatedRig.enabled=(!activated || success) && rigWasEnabled;
                }
                foreach (var collider in gatedButtons) if (collider) collider.enabled=true;
                gatedButtons.Clear(); gatedRig=null; rigSession=null;
                if (!success)
                {
                    LastError=LastError??"Transition interrupted. Please try again.";
                    ShellPause.ReturningToExplore=false;
                    if (activated || FindFirstObjectByType<NativeEncounterSession>()) ShellPause.Ensure().ShowTransitionFailure(LastError,!activated);
                    Debug.LogWarning("Shell transition failed: "+LastError);
                }
                Finished?.Invoke(success,LastError);
            }
        }
        bool DesktopPreview()
        {
#if UNITY_EDITOR
            SubsystemManager.GetSubsystems(displays);
            return !displays.Exists(display=>display.running);
#else
            return false;
#endif
        }
        void GateOfficeInput()
        {
            var rig=FindFirstObjectByType<EncounterOfficeRig>();
            if (!rig) { gatedRig=null; rigSession=null; transitionInput.enabled=false; return; }
            gatedRig=rig; rigWasEnabled=rig.enabled || Paused; rigSession=rig.session;
            var session=FindFirstObjectByType<NativeEncounterSession>(); if(session) session.StopVoice();
            rig.enabled=false;
            // The rig must run to align, but cannot accept office actions or hold-to-talk under black.
            rig.session=null;
            foreach (var button in FindObjectsByType<EncounterOfficeButton>(FindObjectsSortMode.None))
                foreach (var collider in button.GetComponents<Collider>())
                    if (collider.enabled) { gatedButtons.Add(collider); collider.enabled=false; }
            transitionInput.head=rig.head; transitionInput.origin=rig.origin;
            transitionInput.driveHead=true; transitionInput.enabled=true; transitionInput.Release();
        }
        void ClearTitle()
        {
            if (titleRoot) { Destroy(titleRoot.gameObject); titleRoot=null; }
        }
        void CreateFade()
        {
            if (!fade)
            {
                fade=GameObject.CreatePrimitive(PrimitiveType.Quad).transform; fade.name="StereoFade"; fade.SetParent(transform,false);
                Destroy(fade.GetComponent<Collider>());
                fadeMaterial=new Material(Shader.Find("Scalpal/Shell/Fade")); fade.GetComponent<Renderer>().sharedMaterial=fadeMaterial;
                fade.GetComponent<Renderer>().shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
                fade.localScale=new Vector3(4,4,1);
            }
            fade.gameObject.SetActive(true); fadeMaterial.color=Color.clear; PositionFade();
        }
        void LateUpdate()
        {
            if (Busy) PositionFade();
        }
        void PositionFade()
        {
            if (fade) fade.gameObject.SetActive(Busy && !Paused);
            if (titleRoot) titleRoot.gameObject.SetActive(!Paused);
            var head=Camera.main; if (!fade || !head) return;
            fade.SetPositionAndRotation(head.transform.position+head.transform.forward*Mathf.Max(.1f,head.nearClipPlane+.02f),head.transform.rotation);
        }
        IEnumerator Fade(float start,float end,float seconds)
        {
            float elapsed=0;
            while(elapsed<seconds)
            { if (!Paused) elapsed+=Time.unscaledDeltaTime; fadeMaterial.color=new Color(0,0,0,Mathf.Lerp(start,end,elapsed/seconds)); PositionFade(); yield return null; }
            fadeMaterial.color=new Color(0,0,0,end);
        }
        public void CreateTitle(string title)
        {
            var head=Camera.main; if (!head || !ShellView.Configured) return;
            titleRoot=new GameObject("WorldLockedPhaseTitle").transform; titleRoot.SetParent(transform,false);
            if (!ScalpalPlacement.Place(titleRoot,head.transform,1.3f,0)) return;
            // Brand overlay materials (ZTest Always) keep the world-locked title readable above the black stereo fade.
            var heading=ShellView.Text(titleRoot,title,new Vector3(0,0,0),.038f,1.2f,TextAnchor.MiddleCenter,ScalpalTextRole.Title,true);
            heading.name="PhaseTitle";

        }
    }
}
