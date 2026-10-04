using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Scalpal.Surgery
{
    // Additive OR composition; selected procedure comes from the existing run/exercise binding.
    // No patient/procedure ID is hardcoded, and no new attempt/progression authority is introduced.
    [DefaultExecutionOrder(130)]
    public sealed class OpenSurgerySession : MonoBehaviour
    {
        public Transform rightAsis, umbilicus;
        public bool judgeFastPath;
        // Scene-authored mobilization for this atlas: the cecum is delivered with its appendix,
        // mesentery and artery into the wound. The mechanic itself is organ-agnostic.
        public MobileOrganGroup[] mobileOrganGroups = { new MobileOrganGroup {
            partIds = new[]{ "cecum", "appendix", "mesoappendix", "appendicular_artery" }, deliveryPartId = "appendix" } };
        NativeCaseSession session;
        OpenBodyInteraction interaction;
        OpenWoundView wound;
        OpenSurgeryCoach delivery;
        SurgeryFeedback feedback;
        OpenBodyBleeding bleeding;
        OpenSurgeryPanel panel;
        BodyState body;
        GameObject kit, riskAnatomy;
        Transform woundFrame;
        bool premarked;
        int closedLayers;
        float closingClock;
        public string Status { get; private set; } = "Waiting for an open-body case";
        public OpenBodyInteraction Interaction => interaction;
        public OpenWoundView Wound => wound;
        public bool Ready => session && session.Practicing && session.RegistrationReady && session.exercise && session.exercise.CanScore && !session.exercise.Completed && GetComponent<NativeProcedureInput>() && GetComponent<NativeProcedureInput>().InteractionReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded -= Loaded; SceneManager.sceneLoaded += Loaded;
            Attach();
        }
        static void Loaded(Scene scene, LoadSceneMode mode) => Attach();
        static void Attach()
        {
            foreach (var candidate in FindObjectsByType<NativeCaseSession>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (!candidate.GetComponent<OpenSurgerySession>()) candidate.gameObject.AddComponent<OpenSurgerySession>();
        }
        void Awake() { session = GetComponent<NativeCaseSession>(); }
        void Update()
        {
            if (!session || !session.exercise || session.exercise.SelectedCase?.procedure?.openBody?.version != 1) return;
            if (session.exercise.Body != body) ConfigureAttempt();
            if (body == null || !interaction) return;
            bool valid = Ready;
            wound.SetRegistrationValid(valid);
            if (!valid) { interaction.Simulate(0); bleeding.Simulate(0); return; }
            float dt=Time.deltaTime;
            if(!float.IsFinite(dt)||dt<=0||dt>.1f){interaction.Simulate(0);bleeding.Simulate(0);return;}
            bleeding.Prime();
            interaction.Simulate(dt);
            bleeding.Simulate(dt);
            if ((judgeFastPath || session.exercise.SelectedCase.procedure.openBody.fastPathPremarked) && !premarked) Premark();
            AutoClose(Time.deltaTime);
            wound.Apply(body);

        }
        void ConfigureAttempt()
        {
            body = session.exercise.Body; if (body == null) return;
            premarked = false; closedLayers = 0; closingClock = 0;
            if (!kit)
            {
                var prefab = Resources.Load<GameObject>("OpenSurgeryInstruments");
                if (!prefab) { Status = "Open instrument kit missing"; return; }
                kit = Instantiate(prefab); kit.name = "OpenSurgeryCaseTools";
                var reference = session.workbench.tools != null && session.workbench.tools.Length > 0 ? session.workbench.tools[0].transform : transform;
                kit.transform.position = reference.position + new Vector3(0,.02f,.32f);
                session.workbench.RegisterAdditionalTools(kit.GetComponentsInChildren<InstrumentBehaviour>(true));
            }
            if(!riskAnatomy)
            {
                var risks=Resources.Load<GameObject>("OpenSurgeryRisks");
                if(risks)riskAnatomy=Instantiate(risks,session.anatomy.transform,false);
            }
            session.anatomy.RebuildIndex();
            var visibleIds=new HashSet<string>(session.exercise.SelectedCase.procedure.structures ?? Array.Empty<string>());
            foreach(var tissue in body.Tissues)
                foreach(var id in tissue.structureIds != null && tissue.structureIds.Length>0 ? tissue.structureIds : new[]{tissue.id})
                    if(session.anatomy.TryGetPart(id,out _))visibleIds.Add(id);
            session.anatomy.SetExerciseParts(visibleIds);
            if (!woundFrame)
            {
                woundFrame = new GameObject("RegisteredOpenTeachingWound").transform;
                woundFrame.SetParent(session.patientFrame,false);
                // Authored landmark proxies until the registration owner supplies actual ASIS/umbilicus
                // transforms. These are not claims that MediaPipe resolves these bony landmarks.
                Vector3 hip = rightAsis ? session.patientFrame.InverseTransformPoint(rightAsis.position) : new Vector3(-.13f,-.015f,-.14f);
                Vector3 navel = umbilicus ? session.patientFrame.InverseTransformPoint(umbilicus.position) : Vector3.zero;
                woundFrame.localPosition = OpenSurgeryStroke.McBurney(hip,navel);
                woundFrame.localRotation = Quaternion.Euler(90,0,-35);
                wound = woundFrame.gameObject.AddComponent<OpenWoundView>(); wound.Build();
            }
            var plan = session.exercise.SelectedCase.procedure.openBody;
            if (plan.decisions != null && plan.decisions.Length > 0) wound.SetDecisionChoices(plan.decisions[0].choices);
            interaction = GetComponent<OpenBodyInteraction>() ?? gameObject.AddComponent<OpenBodyInteraction>();
            interaction.Submitted -= Applied; interaction.MarkerChanged -= Marked;
            interaction.Initialize(session.exercise,session.workbench.tools,session.patientFrame,woundFrame,()=>Ready);
            bool mobile = interaction.ConfigureMobility(mobileOrganGroups,out string mobility);
            bool anatomyBound = OpenSurgeryAnatomy.Bind(session.anatomy,interaction);
            var volume = GetComponent<NativeVolumeSimulation>();
            if(volume)volume.Initialize(woundFrame,session.workbench,()=>Ready,true);
            // Wall layer contact, grips, tent lift, muscle split and cut evidence come from this volume.
            interaction.BindWall(volume);
            session.workbench.ToolsReset-=ClearPlacements; session.workbench.ToolsReset+=ClearPlacements;
            interaction.Submitted += Applied; interaction.MarkerChanged += Marked;
            if(rightAsis && umbilicus) interaction.SetLandmarks(rightAsis.position,umbilicus.position,woundFrame.right);
            // The old vessel demonstration cannot emit a second unrelated blood pool in this case.
            var legacyVessel = GetComponent<NativeVesselSimulation>(); if(legacyVessel) legacyVessel.enabled=false;
            panel=GetComponent<OpenSurgeryPanel>()??gameObject.AddComponent<OpenSurgeryPanel>();
            panel.Initialize(this,session,woundFrame);
            // Existing authored anatomy remains the source of organ contact/deformation. Missing base
            // references remain explicitly unmeasured instead of being guessed from a bounding box.
            feedback = wound.GetComponent<SurgeryFeedback>() ?? wound.gameObject.AddComponent<SurgeryFeedback>();
            feedback.ResetHistory();
            delivery = GetComponent<OpenSurgeryCoach>() ?? gameObject.AddComponent<OpenSurgeryCoach>();
            delivery.Initialize(session.coach,session.exercise,session.voice);
            bleeding = GetComponent<OpenBodyBleeding>() ?? gameObject.AddComponent<OpenBodyBleeding>();
            bleeding.Initialize(interaction,session.exercise,woundFrame);
            Status = rightAsis && umbilicus ? "Registered landmarks bound" : "Authored landmark proxies; ASIS/umbilicus calibration pending";
            if(!anatomyBound)Status+="; one or more surgical base references missing";
            if(!mobile)Status+="; organ mobilization unavailable: "+mobility;
            if(OpenSurgeryAnatomy.DeliveryReachBound(session.anatomy,woundFrame,out float needed,out float maximum))
            {
                float tether=0;
                foreach(var group in interaction.Mobility) if(group.Contains(session.anatomy.TryGetPart("appendix",out var part)?part.transform:null)) tether=Mathf.Max(tether,group.Definition.maxTravelMm);
                Status+=$"; delivery needs {needed:F1} mm, cage bound {maximum:F1} mm, mobilization tether {tether:F1} mm";Debug.Log("SCALPAL_OPEN_DELIVERY_BOUND "+Status);
            }
        }
        void Marked(IReadOnlyList<Vector3> points) => wound.SetMarker(points);
        void Applied(BodyRecord record, InstrumentBehaviour tool)
        {
            XRNode? hand = null;
            if(tool) foreach(var input in session.workbench.inputs)
                if(input && input.GetComponent<InstrumentInteractor>()?.HeldInstrument == tool) { hand=input.controller; break; }
            feedback.Play(record,hand);
            // No success event is manufactured by the presentation.
            wound.Apply(body);
        }
        void Premark()
        {
            var action = interaction.CreateMeasurement("skin_marker","mark","skin",woundFrame.position,"assistant-fastpath");
            if (action == null) return;
            action.lengthMm = 60; action.choice = "assisted_premark";
            if (!interaction.SubmitMeasured(action)) return;
            interaction.SetMarkedLine(woundFrame.TransformPoint(Vector3.left*.03f),woundFrame.TransformPoint(Vector3.right*.03f)); premarked = true;
        }
        void AutoClose(float seconds)
        {
            var plan = session.exercise.SelectedCase.procedure.openBody;
            foreach (var milestone in plan.milestones)
            {
                if (milestone.id == "close") continue;
                foreach (var predicate in milestone.predicates) if (!body.Test(predicate)) return;
            }
            closingClock += seconds;
            if (closingClock < .35f) return;
            closingClock = 0;
            // Closure is an explicitly authored assistant action, recorded per layer.
            string[] layers = {"peritoneum","muscle","fascia","fat","skin"};
            if (closedLayers >= layers.Length) return;
            var action = interaction.CreateMeasurement("assistant","close",layers[closedLayers],woundFrame.position,"assistant-close");
            if (action != null && interaction.SubmitMeasured(action)) closedLayers++;
        }
        void ClearPlacements(){ if(interaction)interaction.ClearPlacements(); }
        public bool CanPremark => Ready && body != null && body.Get("skin","marked")==0 && body.Get("skin","opened")==0;
        public bool EnableJudgeFastPath()
        {
            if(!CanPremark)return false;
            judgeFastPath=true; Premark(); return premarked;
        }
        public bool ChooseBase(string choice) => interaction && interaction.Choose(wound.decisionTissueId,choice);
        public bool FinishAttempt() => Ready && session.exercise.Submit(CaseEvent.Finish(),out _,out _);
        void OnDestroy()
        {
            if(session && session.workbench)session.workbench.ToolsReset-=ClearPlacements;
            if (interaction) { interaction.Submitted -= Applied; interaction.MarkerChanged -= Marked; }
            if (woundFrame) { if(Application.isPlaying)Destroy(woundFrame.gameObject);else DestroyImmediate(woundFrame.gameObject); }
            // Kit tools are owned by the scene/workbench; do not leave dangling registered entries.
        }
    }
}
