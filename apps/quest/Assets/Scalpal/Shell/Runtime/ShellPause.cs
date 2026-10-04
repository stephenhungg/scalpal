using System.Collections.Generic;
using Scalpal.Brand;
using Scalpal.EncounterOffice;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace Scalpal.Shell
{
    // Persistent presentation pause. The encounter engine remains the sole authority for clinical state.
    public sealed class ShellPause : MonoBehaviour
    {
        public static ShellPause Instance { get; private set; }
        public static bool ReturningToExplore;
        public bool IsPaused { get; private set; }
        ShellInput hubInput, pauseInput;
        EncounterOfficeRig officeRig;
        Transform panel;
        float savedScale = 1;
        bool previousMenu, menuArmed, focused = true, suspended, wasPresent = true, confirming;
        bool savedAudioPause;
        bool officeWasEnabled, menuTransition, officeAligned;
        string transitionFailure;
        bool failureCanResume;
        // Office: voice on/off is chosen here (the room has no control panel); it applies on Resume.
        bool officeVoiceCaptured, voiceOnResume, confirmingSkip, confirmingEnd;
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        readonly List<XRInputSubsystem> subscribed = new List<XRInputSubsystem>();
        public static ShellPause Ensure()
        {
            if (Instance) return Instance;
            return new GameObject("ScalpalGlobalPause").AddComponent<ShellPause>();
        }
        void Awake()
        {
            if (Instance && Instance != this) { Destroy(gameObject); return; }
            Instance = this; DontDestroyOnLoad(gameObject);
            pauseInput = gameObject.AddComponent<ShellInput>(); pauseInput.enabled = false;
            SceneManager.sceneLoaded += SceneLoaded;
        }
        void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            foreach (var system in subscribed) system.trackingOriginUpdated -= TrackingOriginUpdated;
            if (Instance == this) { Instance = null; if (IsPaused) { Time.timeScale = savedScale; AudioListener.pause=savedAudioPause; } }
        }
        public void Configure(ShellInput input) { hubInput = input; }
        public void RememberOfficeInput(bool wasEnabled) { officeWasEnabled = wasEnabled; }
        void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            officeRig = FindFirstObjectByType<EncounterOfficeRig>(); officePresentationYaw = 0; officeAligned=false;
            if (IsPaused) { SuspendOffice(); BuildMenu(); }
        }
        public void MarkOfficeAligned() { officeAligned=true; }
        void TrackingOriginUpdated(XRInputSubsystem system)
        {
            if (officeRig && (!officeAligned || !officeRig.Ready)) return;
            if (hubInput && !hubInput.Ready) return;
            Recenter();
        }
        void Update()
        {
            if (officeRig && officeRig.Ready) officeAligned=true;
            SubsystemManager.GetSubsystems(inputs);
            foreach (var system in inputs)
                if (!subscribed.Contains(system)) { subscribed.Add(system); system.trackingOriginUpdated += TrackingOriginUpdated; }
            if (IsPaused && (!panel || menuTransition != ShellTransition.Busy)) BuildMenu();
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            left.TryGetFeatureValue(CommonUsages.menuButton, out bool menu);
            if (!menu) menuArmed = true;
            if (menuArmed && menu && !previousMenu && (!ShellTransition.Busy || IsPaused)) { if (IsPaused) Resume(); else Pause(); }
            previousMenu = menu;
#if UNITY_EDITOR
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame && (!ShellTransition.Busy || IsPaused)) { if (IsPaused) Resume(); else Pause(); }
#endif
            var headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (headDevice.isValid && headDevice.TryGetFeatureValue(CommonUsages.userPresence, out bool present))
            { if (!present && wasPresent) Pause(); wasPresent = present; }
        }
        public void Pause()
        {
            if (IsPaused) return;
            IsPaused = true; savedScale = Time.timeScale; Time.timeScale = 0; savedAudioPause=AudioListener.pause; AudioListener.pause=true; confirming = false;
            if (hubInput) hubInput.Release(); SuspendOffice(); BuildMenu();
        }
        void SuspendOffice()
        {
            officeRig = FindFirstObjectByType<EncounterOfficeRig>();
            if (officeRig) { officeWasEnabled = officeRig.enabled; officeRig.enabled = false; }
            var session = FindFirstObjectByType<NativeEncounterSession>();
            if (session) { if (!officeVoiceCaptured) { voiceOnResume = session.VoiceEnabled; officeVoiceCaptured = true; } session.StopVoice(); }
        }
        public void Resume()
        {
            // Focus/headset return alone never resumes. A deliberate menu action is required.
            if (!IsPaused || !focused || suspended || !wasPresent || (!string.IsNullOrEmpty(transitionFailure) && !failureCanResume)) return;
            transitionFailure=null;
            IsPaused = false; confirming = confirmingSkip = confirmingEnd = false; Time.timeScale = savedScale; AudioListener.pause=savedAudioPause;
            var office = FindFirstObjectByType<NativeEncounterSession>();
            if (office && officeVoiceCaptured && voiceOnResume && office.State?.phase == "interview") office.ToggleVoice();
            officeVoiceCaptured = false;
            if (panel) { Discard(panel.gameObject); panel = null; }
            if (pauseInput) { pauseInput.Release(); pauseInput.enabled = false; }
            if (officeRig) officeRig.enabled = officeWasEnabled;
            if (hubInput) hubInput.Release();
        }
        public void ShowTransitionFailure(string message,bool canResume=false)
        {
            transitionFailure=message; failureCanResume=canResume;
            Pause(); BuildMenu();
        }
        void BuildMenu()
        {
            if (panel) Discard(panel.gameObject);
            var head = Camera.main; if (!head || !ShellView.Configured) return;
            menuTransition=ShellTransition.Busy;
            panel = ShellView.Panel(transform, "Paused", Vector3.zero, new Vector2(.82f,.67f));
            ShellView.Text(panel, confirming ? "Leave this encounter?" : string.IsNullOrEmpty(transitionFailure) ? "Paused" : "Unable to begin", new Vector3(-.36f,.285f,-.012f), .040f,.72f,TextAnchor.UpperLeft,ScalpalTextRole.Title);
            if (!string.IsNullOrEmpty(transitionFailure) && !confirming)
            {
                ShellView.Text(panel,EncounterOfficePanel.Wrap(transitionFailure,42),new Vector3(-.36f,.10f,-.012f),.025f,.72f);
                if (failureCanResume) ShellView.Button(panel,"Resume previous scene",new Vector3(0,-.11f,-.014f),new Vector2(.66f,.075f),Resume);
                else ShellView.Button(panel,"Recenter menu",new Vector3(0,-.11f,-.014f),new Vector2(.66f,.075f),()=>PlaceMenu(Camera.main));
                ShellView.Button(panel,"Back to explore",new Vector3(0,-.22f,-.014f),new Vector2(.66f,.075f),BackToExplore,!ShellTransition.Busy);
            }
            else if (confirming)
            {
                ShellView.Text(panel, "Return to the patient grid.\nA new encounter starts next time.",new Vector3(-.36f,.09f,-.012f),.025f,.72f);
                ShellView.Button(panel,"Keep practising",new Vector3(0,-.09f,-.014f),new Vector2(.66f,.07f),()=>{confirming=false;BuildMenu();});
                ShellView.Button(panel,"Back to explore",new Vector3(0,-.20f,-.014f),new Vector2(.66f,.07f),BackToExplore);
            }
            else
            {
                ShellView.Button(panel,"Resume",new Vector3(0,.05f,-.014f),new Vector2(.66f,.075f),Resume,true,true);
                ShellView.Button(panel,ShellTransition.Busy?"Back after loading":"Back to explore",new Vector3(0,-.06f,-.014f),new Vector2(.66f,.075f),()=>{confirming=true;BuildMenu();},!ShellTransition.Busy);
                ShellView.Button(panel,"Recenter",new Vector3(0,-.17f,-.014f),new Vector2(.66f,.075f),Recenter);
                var office = FindFirstObjectByType<NativeEncounterSession>();
                if (office && office.State != null && office.State.phase == "interview")
                {
                    ShellView.Button(panel,voiceOnResume?"Voice on · select for off":"Voice off · select for on",new Vector3(-.17f,-.28f,-.014f),new Vector2(.32f,.075f),()=>{voiceOnResume=!voiceOnResume;BuildMenu();});
                    ShellView.Button(panel,confirmingSkip?"Confirm skip":"Skip to surgery",new Vector3(.17f,-.28f,-.014f),new Vector2(.32f,.075f),SkipToSurgery,!ShellTransition.Busy,false,true);
                }
                // Operating room (AR or VR) once practice has started: end the case and go to the recap. Asks once.
                var surgery = FindFirstObjectByType<Scalpal.Surgery.OpenSurgerySession>();
                if (surgery && Scalpal.Handoff.HandoffRun.Current?.practiceStarted == true)
                    ShellView.Button(panel,confirmingEnd?"Confirm end surgery":"End surgery",new Vector3(0,-.28f,-.014f),new Vector2(.66f,.075f),EndSurgery,!ShellTransition.Busy,false,true);
            }
            pauseInput.head = head; pauseInput.origin = officeRig ? officeRig.origin : hubInput ? hubInput.origin : head.transform.parent;
            pauseInput.allowedRoot = panel; pauseInput.content = null; pauseInput.enabled = true; pauseInput.Release(); PlaceMenu(head);
        }
        void EndSurgery()
        {
            if (!confirmingEnd) { confirmingEnd = true; BuildMenu(); return; }
            var surgery = FindFirstObjectByType<Scalpal.Surgery.OpenSurgerySession>();
            Resume();
            if (surgery) surgery.RequestFinish();
        }
        static void Discard(GameObject value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
        // Office pause: "Skip to surgery" asks once, then leaves the interview for the Theatre card.
        public void SkipToSurgery()
        {
            var office = FindFirstObjectByType<NativeEncounterSession>();
            if (!office || office.State?.phase != "interview") return;
            if (!confirmingSkip) { confirmingSkip = true; BuildMenu(); return; }
            voiceOnResume = false; Resume();
            office.SkipToSurgery();
        }
        void PlaceMenu(Camera head)
        {
            if (!panel || !head) return;
            var forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .5f) forward = Vector3.forward;
            // Level with the horizon: yaw-only facing, never the head's pitch or roll.
            panel.SetPositionAndRotation(head.transform.position + forward * .7f + Vector3.down*.05f,ScalpalPlacement.Level(forward));
            // Paused at timeScale 0 no physics step runs, so new/moved button colliders are invisible to the
            // pointer raycast until synced (the Quest pause menu looked fine but ignored every press).
            Physics.SyncTransforms();
        }
        public void Recenter()
        {
            if (hubInput) hubInput.Recenter();
            if (DialogueBox.Active) DialogueBox.Active.Recenter();
            // Office tracking origin is owned by its rig. Move presentation roots, never its tracked camera.
            if (officeAligned && officeRig && officeRig.head && officeRig.origin)
            {
                var head = officeRig.head.transform;
                var roomForward = officeRig.origin.rotation * Vector3.forward;
                var viewForward = Vector3.ProjectOnPlane(head.forward,Vector3.up).normalized;
                float angle = Mathf.DeltaAngle(officePresentationYaw, Vector3.SignedAngle(roomForward, viewForward, Vector3.up));
                var rotation = Quaternion.AngleAxis(angle,Vector3.up);
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                {
                    if (root == officeRig.gameObject || head.IsChildOf(root.transform)) continue;
                    root.transform.position = head.position + rotation * (root.transform.position-head.position);
                    root.transform.rotation = rotation * root.transform.rotation;
                }
                // Record presentation facing separately: no XR origin/camera movement.
                officePresentationYaw += angle;
            }
            PlaceMenu(Camera.main); if (pauseInput) pauseInput.Release();
        }
        float officePresentationYaw;
        void BackToExplore()
        {
            if (ShellTransition.Busy) return;
            transitionFailure=null; Resume();
            if (IsPaused) return;
            ReturningToExplore = true;
            StartCoroutine(ShellTransition.Ensure().Load("Launch", "Explore · Choose a synthetic patient"));
        }
        void OnApplicationFocus(bool value) { focused = value; if (!value) { menuArmed=false; Pause(); } }
        void OnApplicationPause(bool value) { suspended = value; if (value) { menuArmed=false; Pause(); } }
    }
}
