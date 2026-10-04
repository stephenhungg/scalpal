using System;
using System.Collections;
using System.Collections.Generic;
using Scalpal.EncounterOffice;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.Shell
{
    // One-shot, explicit Explore -> Office contract. No encounter/attempt is invented in the shell.
    public sealed class ShellTransition : MonoBehaviour
    {
        public sealed class SelectedPatient
        {
            public readonly string patientId, serviceUrl;
            public SelectedPatient(string id, string url) { patientId=id; serviceUrl=url; }
        }
        static ShellTransition instance;
        static SelectedPatient selected;
        public static bool Busy { get; private set; }
        public static string LastError { get; private set; }
        Material fadeMaterial;
        Transform fade, titleRoot;
        readonly List<Material> titleMaterials = new List<Material>();
        ShellInput transitionInput;
        EncounterOfficeRig gatedRig;
        bool rigWasEnabled;
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
        public static bool SupportsPatient(string id) => id==EncounterContract.FemalePatientId || id==EncounterContract.MalePatientId;
        public static bool TryConsumeSelection(out SelectedPatient value)
        { value=selected; selected=null; return value!=null && SupportsPatient(value.patientId); }
        public static bool TryStageSelection(string patientId,string serviceUrl)
        {
            if (Busy || !SupportsPatient(patientId)) return false;
            if (!Uri.TryCreate(serviceUrl,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https")) return false;
            selected=new SelectedPatient(patientId,serviceUrl.TrimEnd('/')); return true;
        }
        public bool BeginOffice(string patientId,string title,string serviceUrl)
        {
            if (Busy || !SupportsPatient(patientId)) return false;
            if (!Uri.TryCreate(serviceUrl,UriKind.Absolute,out var uri) || (uri.Scheme!="http" && uri.Scheme!="https")) return false;
            if (!Application.CanStreamedLevelBeLoaded("DiagnosisOffice")) { LastError="Diagnosis office is missing from this build."; return false; }
            if (!TryStageSelection(patientId,serviceUrl)) return false;
            StartCoroutine(Load("DiagnosisOffice",title,StartSelectedEncounter)); return true;
        }
        static void StartSelectedEncounter()
        {
            if (!TryConsumeSelection(out var handoff)) throw new InvalidOperationException("The selected patient handoff is missing or already consumed.");
            var office=FindFirstObjectByType<NativeEncounterSession>();
            if (!office) throw new InvalidOperationException("DiagnosisOffice has no NativeEncounterSession.");
            // Runs after Start's private endpoint config read, preserving the explicit Explore service choice.
            office.baseUrl=handoff.serviceUrl; office.StartPatient(handoff.patientId);
        }
        public IEnumerator Load(string scene,string title,Action afterLoad=null)
        {
            if (Busy) yield break;
            LastError=null;
            if (!Application.CanStreamedLevelBeLoaded(scene)) { selected=null; LastError="Scene unavailable: "+scene; Debug.LogError(LastError); yield break; }
            Busy=true;
            try
            {
                GateOfficeInput(); CreateFade(); yield return Fade(0,1,.4f);
                var operation=SceneManager.LoadSceneAsync(scene,LoadSceneMode.Single);
                if (operation==null) { LastError="Scene loading did not start: "+scene; yield break; }
                operation.allowSceneActivation=false;
                while (operation.progress<.9f) yield return null;
                operation.allowSceneActivation=true;
                while (!operation.isDone) yield return null;
                // Scene Start callbacks initialize bindings/configuration before the handoff is consumed.
                yield return null;
                GateOfficeInput();
                // Suspension requires an explicit Resume before creating a patient encounter.
                // Reveal the paused scene/menu rather than hiding its Resume button behind black.
                while (ShellPause.Instance && ShellPause.Instance.IsPaused)
                { fade.gameObject.SetActive(false); yield return null; }
                fade.gameObject.SetActive(true);
                if (gatedRig && rigWasEnabled)
                {
                    // Give a newly suspended office rig one initialization frame behind black.
                    gatedRig.enabled=true; yield return null; GateOfficeInput();
                }
                try { afterLoad?.Invoke(); }
                catch (Exception exception) { LastError=exception.Message; Debug.LogError("Shell handoff failed: "+exception.Message); }
                PositionFade(); CreateTitle(string.IsNullOrEmpty(LastError)?title:"Unable to begin\n"+LastError);
                yield return new WaitForSecondsRealtime(.8f);
                ClearTitle();
                yield return Fade(1,0,.4f);
            }
            finally
            {
                selected=null; Busy=false;
                if (fade) fade.gameObject.SetActive(false);
                ClearTitle();
                if (transitionInput) transitionInput.enabled=false;
                if (gatedRig)
                {
                    if (ShellPause.Instance && ShellPause.Instance.IsPaused) ShellPause.Instance.RememberOfficeInput(rigWasEnabled);
                    else gatedRig.enabled=rigWasEnabled;
                }
                gatedRig=null;
            }
        }
        void GateOfficeInput()
        {
            var rig=FindFirstObjectByType<EncounterOfficeRig>();
            if (!rig) { gatedRig=null; transitionInput.enabled=false; return; }
            gatedRig=rig; rigWasEnabled=rig.enabled || (ShellPause.Instance && ShellPause.Instance.IsPaused);
            var session=FindFirstObjectByType<NativeEncounterSession>(); if(session) session.StopVoice();
            rig.enabled=false;
            transitionInput.head=rig.head; transitionInput.origin=rig.origin;
            transitionInput.enabled=true; transitionInput.Release();
        }
        void ClearTitle()
        {
            if (titleRoot) { Destroy(titleRoot.gameObject); titleRoot=null; }
            foreach(var material in titleMaterials) if(material) Destroy(material);
            titleMaterials.Clear();
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
            // Dynamic Inter atlas recreation must update the overlay copies as well.
            if (ShellView.Font) foreach(var material in titleMaterials) if(material) material.mainTexture=ShellView.Font.material.mainTexture;
        }
        void PositionFade()
        {
            var head=Camera.main; if (!fade || !head) return;
            fade.SetPositionAndRotation(head.transform.position+head.transform.forward*Mathf.Max(.1f,head.nearClipPlane+.02f),head.transform.rotation);
        }
        IEnumerator Fade(float start,float end,float seconds)
        {
            float elapsed=0;
            while(elapsed<seconds)
            { elapsed+=Time.unscaledDeltaTime; fadeMaterial.color=new Color(0,0,0,Mathf.Lerp(start,end,elapsed/seconds)); yield return null; }
            fadeMaterial.color=new Color(0,0,0,end);
        }
        void CreateTitle(string title)
        {
            var head=Camera.main; if (!head) return;
            titleRoot=new GameObject("WorldLockedPhaseTitle").transform; titleRoot.SetParent(transform,false);
            var forward=Vector3.ProjectOnPlane(head.transform.forward,Vector3.up).normalized;
            titleRoot.SetPositionAndRotation(head.transform.position+forward*1.3f,Quaternion.LookRotation(forward));
            var heading=ShellView.Text(titleRoot,title,new Vector3(0,.08f,0),.038f,1.2f,TextAnchor.MiddleCenter);
            var stepper=ShellView.Text(titleRoot,"Explore  ›  Office  ›  OR  ›  Replay  ›  Recap",new Vector3(0,-.11f,0),.024f,1.2f,TextAnchor.MiddleCenter);
            foreach (var text in new[]{heading,stepper})
            {
                var material=text.GetComponent<Renderer>().material; material.shader=Shader.Find("Scalpal/Shell/Overlay Text"); material.renderQueue=4200; titleMaterials.Add(material);
                // A separate overlay text pass keeps the world-locked title readable above the black stereo fade.
            }
        }
    }
}
