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
            partIds = new[]{ "cecum", "appendix", "mesoappendix", "appendicular_artery" }, deliveryPartId = "appendix", maxGripRotationDegrees = 80 } };
        NativeCaseSession session;
        OpenBodyInteraction interaction;
        OpenWoundView wound;
        OpenSurgeryCoach delivery;
        SurgeryFeedback feedback;
        OpenBodyBleeding bleeding;
        PatientIncisions incisions;
        SurgeryBlood blood;
        SurgeryTriggerHint triggerHint;
        MarkingGuide guide;
        SurgicalOrganAppearance organAppearance;
        SurgicalActionAppearance actionAppearance;
        SurgicalClosureAppearance closureAppearance;
        NativeOperatingRoomLighting surgicalLighting;
        NativePatientSurfaceShading patientShading;
        Collider patientSkin;
        bool skinProjectionVirtual;
        readonly List<GameObject> hiddenPorts = new List<GameObject>();
        GameObject toolTable;
        OpenSurgeryPanel panel;
        BodyState body;
        GameObject kit, riskAnatomy;
        Transform woundFrame;
        readonly Dictionary<XRNode, InstrumentBehaviour> inHand = new Dictionary<XRNode, InstrumentBehaviour>();
        bool premarked;
        int closedLayers;
        float closingClock;
        public string Status { get; private set; } = "Waiting for an open-body case";
        public OpenBodyInteraction Interaction => interaction;
        public OpenWoundView Wound => wound;
        public MarkingGuide Guide => guide;
        // Torso-frame proxy for the right ASIS when no registered landmark is bound; the wound sits a third of the way to the umbilicus origin.
        public static readonly Vector3 AuthoredRightAsis = new Vector3(-.13f,-.015f,-.14f);
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
            if (!session || !session.exercise || session.exercise.SelectedCase?.procedure?.openBody?.version != 1) { ShowPorts(); return; }
            if (session.exercise.Body != body) ConfigureAttempt();
            if (body == null || !interaction) return;
            bool virtualSkin=VirtualBody();
            if(virtualSkin!=skinProjectionVirtual){skinProjectionVirtual=virtualSkin;wound.BindSkinSurface(virtualSkin?patientSkin:null);}
            ReportHands();
            // The separate tool table holds nothing in the open case: the instrument stand is the tool surface in both modes.
            if (toolTable && toolTable.activeSelf) toolTable.SetActive(false);
            bool valid = Ready;
            if (finishRequested && valid) { finishRequested = false; FinishAttempt(); }
            wound.SetRegistrationValid(valid);
            if (!valid) { interaction.Simulate(0); bleeding.Simulate(0); incisions.Simulate(0); blood.Simulate(0); return; }
            float dt=Time.deltaTime;
            if(!float.IsFinite(dt)||dt<=0||dt>.1f){interaction.Simulate(0);bleeding.Simulate(0);incisions.Simulate(0);blood.Simulate(0);return;}
            bleeding.Prime();
            interaction.Simulate(dt);
            bleeding.Simulate(dt);
            incisions.Simulate(dt);
            blood.Simulate(dt);
            if ((judgeFastPath || session.exercise.SelectedCase.procedure.openBody.fastPathPremarked) && !premarked) Premark();
            AutoClose(Time.deltaTime);
            wound.Apply(body);

        }
        void ConfigureAttempt()
        {
            body = session.exercise.Body; if (body == null) return;
            // Release presentation-only closure overrides before resetting the shared attempt.
            if (closureAppearance) closureAppearance.Dispose();
            premarked = false; closedLayers = 0; closingClock = 0; inHand.Clear(); finishRequested = false;
            if (!kit)
            {
                var prefab = Resources.Load<GameObject>("OpenSurgeryInstruments");
                if (!prefab) { Status = "Open instrument kit missing"; return; }
                kit = Instantiate(prefab); kit.name = "OpenSurgeryCaseTools";
                var reference = session.workbench.tools != null && session.workbench.tools.Length > 0 ? session.workbench.tools[0].transform : transform;
                kit.transform.position = reference.position + new Vector3(0,.02f,.32f);
                session.workbench.RegisterAdditionalTools(kit.GetComponentsInChildren<InstrumentBehaviour>(true));
            }
            ApplyOpenToolSet();
            if (!toolTable) foreach (var root in gameObject.scene.GetRootGameObjects()) if (root.name == "Workbench" && root.GetComponent<Collider>()) toolTable = root;
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
                Vector3 hip = rightAsis ? session.patientFrame.InverseTransformPoint(rightAsis.position) : AuthoredRightAsis;
                Vector3 navel = umbilicus ? session.patientFrame.InverseTransformPoint(umbilicus.position) : Vector3.zero;
                woundFrame.localPosition = OpenSurgeryStroke.McBurney(hip,navel);
                woundFrame.localRotation = Quaternion.Euler(90,0,-35);
                wound = woundFrame.gameObject.AddComponent<OpenWoundView>(); wound.Build();
            }
            var plan = session.exercise.SelectedCase.procedure.openBody;
            patientSkin=PatientSkinCollider();skinProjectionVirtual=VirtualBody();
            wound.BindSkinSurface(skinProjectionVirtual ? patientSkin : null);
            if (plan.decisions != null && plan.decisions.Length > 0) wound.SetDecisionChoices(plan.decisions[0].choices);
            interaction = GetComponent<OpenBodyInteraction>() ?? gameObject.AddComponent<OpenBodyInteraction>();
            interaction.Submitted -= Applied; interaction.MarkerChanged -= Marked; interaction.Contacted -= Touched; interaction.RegionInjured -= Injured;
            interaction.TouchedWithoutTrigger -= NeedsTrigger;
            interaction.Initialize(session.exercise,session.workbench.tools,session.patientFrame,woundFrame,()=>Ready);
            bool mobile = interaction.ConfigureMobility(mobileOrganGroups,out string mobility);
            bool anatomyBound = OpenSurgeryAnatomy.Bind(session.anatomy,interaction);
            var volume = GetComponent<NativeVolumeSimulation>();
            if(volume)volume.Initialize(woundFrame,session.workbench,()=>Ready,true);
            if(volume)volume.PhysicsSurfaceVisible=()=>!wound||!wound.RenderingWound;
            // Wall layer contact, grips, tent lift, muscle split and cut evidence come from this volume.
            interaction.BindWall(volume);
            session.workbench.ToolsReset-=ClearPlacements; session.workbench.ToolsReset+=ClearPlacements;
            interaction.Submitted += Applied; interaction.MarkerChanged += Marked; interaction.Contacted += Touched; interaction.RegionInjured += Injured;
            interaction.TouchedWithoutTrigger += NeedsTrigger;
            triggerHint = GetComponent<SurgeryTriggerHint>() ?? gameObject.AddComponent<SurgeryTriggerHint>();
            if(rightAsis && umbilicus) interaction.SetLandmarks(rightAsis.position,umbilicus.position,woundFrame.right);
            guide = GetComponent<MarkingGuide>() ?? gameObject.AddComponent<MarkingGuide>();
            guide.Initialize(session,interaction,wound,triggerHint,rightAsis,umbilicus,()=>Ready);
            HidePorts();
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
            bleeding.PresentationVisible=CanDisplayOpenField;
            // Incisions off the field and visible blood flow follow the same attempt; a retry clears both.
            if (!blood)
            {
                var view = new GameObject("SurgeryBlood"); view.transform.SetParent(transform,false);
                blood = view.AddComponent<SurgeryBlood>(); incisions = view.AddComponent<PatientIncisions>();
            }
            incisions.Initialize(interaction,session.patientFrame,woundFrame,PatientSkinCollider(),VirtualBody);
            blood.Initialize(interaction,incisions,session.exercise,session.patientFrame,woundFrame,VirtualBody,GetComponent<NativePatientMonitor>());
            incisions.PresentationVisible=CanDisplayPatient;
            blood.PresentationVisible=CanDisplayPatient;
            organAppearance = GetComponent<SurgicalOrganAppearance>();
            if (!organAppearance) organAppearance = gameObject.AddComponent<SurgicalOrganAppearance>();
            organAppearance.Initialize(session, interaction);
            actionAppearance = GetComponent<SurgicalActionAppearance>();
            if (!actionAppearance) actionAppearance = gameObject.AddComponent<SurgicalActionAppearance>();
            actionAppearance.Initialize(session, interaction, woundFrame);
            closureAppearance = GetComponent<SurgicalClosureAppearance>();
            if (!closureAppearance) closureAppearance = gameObject.AddComponent<SurgicalClosureAppearance>();
            closureAppearance.Initialize(session, interaction, woundFrame);
            patientShading = GetComponent<NativePatientSurfaceShading>();
            if (!patientShading) patientShading = gameObject.AddComponent<NativePatientSurfaceShading>();
            patientShading.Initialize(session.presentation);
            surgicalLighting = GetComponent<NativeOperatingRoomLighting>();
            if (!surgicalLighting) surgicalLighting = gameObject.AddComponent<NativeOperatingRoomLighting>();
            surgicalLighting.Initialize(session);
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
        bool CanDisplayPatient()=>session&&session.anatomy&&session.anatomy.RegistrationValid&&session.anatomy.CanDisplay;
        bool CanDisplayOpenField()=>CanDisplayPatient()&&body!=null&&body.Get("peritoneum","opened")>0&&body.Get("skin","closed")<=0;
        // The open expected path (docs/surgery-procedure.md), in step order. Two rows on the theatre's instrument
        // stand: the first six nearest the learner with their grips toward them, the rest facing back.
        public static readonly string[] OpenToolSet = {
            "skin_marker", "scalpel", "toothed_forceps", "retractor", "retractor", "babcock",
            "hemostat", "hemostat", "right_angle_clamp", "metzenbaum_scissors", "suture_tie", "suction_irrigator", "laparoscope_30" };
        public const string InstrumentStandPath = "RoomCollision/InstrumentStand";
        // Only the expected-path set stays active (unpickable, unlabelled and absent from coach events otherwise);
        // it is laid out tightly on the instrument stand beside the table. The separate tool table is hidden in VR.
        void ApplyOpenToolSet()
        {
            var kept = new InstrumentBehaviour[OpenToolSet.Length];
            foreach (var tool in session.workbench.tools ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!tool) continue;
                int slot = -1;
                for (int i = 0; i < OpenToolSet.Length; i++) if (OpenToolSet[i] == tool.instrumentId && !kept[i]) { slot = i; break; }
                if (slot >= 0) kept[slot] = tool;
                tool.gameObject.SetActive(slot >= 0);
            }
            foreach (var target in session.workbench.targets ?? Array.Empty<TrainingTarget>()) if (target) target.gameObject.SetActive(false);
            // The camera scope shows its view on a panel that follows the learner's gaze while its trigger is held,
            // never on the fixed floating monitor.
            foreach (var tool in session.workbench.tools ?? Array.Empty<InstrumentBehaviour>())
            {
                var scope = tool ? tool.GetComponent<LaparoscopeView>() : null;
                if (!scope) continue;
                scope.followView = true;
                if (scope.monitor) scope.monitor.gameObject.SetActive(false);
            }
            var stand = session.presentation && session.presentation.virtualRoom
                ? session.presentation.virtualRoom.transform.Find(InstrumentStandPath)?.GetComponent<BoxCollider>() : null;
            if (!stand) { Status = "Instrument stand missing; open tools left in place"; return; }
            // AR hides the virtual theatre, so the stand would vanish under the tools: give AR its own visible, solid copy.
            if (session.presentation.passthrough) stand = ArStand(stand);
            Vector3 top = stand.center + Vector3.up * stand.size.y * .5f;
            const float grip = .045f; // handle collider half height
            for (int i = 0; i < kept.Length; i++)
            {
                if (!kept[i]) continue;
                bool near = i < 6, scope = i == 12; int column = near ? i : i - 6;
                // Nearest the learner (stand -Z, +X side) first; the far row is offset half a pitch so long shafts pass between
                // grips. The camera scope lies crosswise along the middle of the stand.
                float x = scope ? 0 : (near ? .125f : .1525f) - column * .055f, z = scope ? 0 : (near ? -1 : 1) * (stand.size.z * .5f - .03f);
                Vector3 position = stand.transform.TransformPoint(top + new Vector3(x, 0, z)) + stand.transform.up * grip;
                Quaternion rotation = stand.transform.rotation * Quaternion.Euler(0, scope ? 90 : near ? 0 : 180, 0);
                session.workbench.SetRestPose(kept[i], position, rotation);
            }
            Physics.SyncTransforms();
        }
        GameObject arStand;
        // A plain steel tray on a pole at the theatre stand's pose: visible over passthrough and solid for dropped tools.
        BoxCollider ArStand(BoxCollider theatre)
        {
            if (!arStand)
            {
                arStand = new GameObject("ArInstrumentStand");
                arStand.transform.SetPositionAndRotation(theatre.transform.position, theatre.transform.rotation);
                arStand.transform.localScale = theatre.transform.lossyScale;
                var box = arStand.AddComponent<BoxCollider>(); box.center = theatre.center; box.size = theatre.size;
                var steel = new Material(Shader.Find("Standard")) { color = new Color(.62f, .64f, .66f) };
                steel.SetFloat("_Metallic", .6f); steel.SetFloat("_Glossiness", .55f);
                Visual(PrimitiveType.Cube, theatre.center + Vector3.up * (theatre.size.y * .5f - .006f), new Vector3(theatre.size.x, .012f, theatre.size.z), steel);
                float floor = session.workbench ? session.workbench.initialHeadFloorPosition.y : 0;
                float height = Mathf.Max(.1f, arStand.transform.TransformPoint(theatre.center).y - floor) / Mathf.Max(1e-3f, arStand.transform.lossyScale.y);
                Visual(PrimitiveType.Cylinder, theatre.center + Vector3.down * height * .5f, new Vector3(.03f, height * .5f, .03f), steel);
                Visual(PrimitiveType.Cylinder, theatre.center + Vector3.down * (height - .01f), new Vector3(.3f, .01f, .3f), steel);
            }
            return arStand.GetComponent<BoxCollider>();
            void Visual(PrimitiveType shape, Vector3 position, Vector3 scale, Material material)
            {
                var part = GameObject.CreatePrimitive(shape); part.transform.SetParent(arStand.transform, false);
                part.transform.localPosition = position; part.transform.localScale = scale;
                DestroyImmediate(part.GetComponent<Collider>()); // the box above is the one support collider
                part.GetComponent<Renderer>().sharedMaterial = material;
            }
        }
        // The marking guide draws the recorded ink on the skin the learner sees (and the live stroke before it).
        void Marked(IReadOnlyList<Vector3> points) { guide.SetInk(points); wound.SetMarker(points); }
        // Laparoscopic port targets mean nothing in the open case and would sit on the belly button; restored for other cases.
        void HidePorts()
        {
            foreach (var port in session.patientFrame.GetComponentsInChildren<NativePortMarker>(true))
                if (port.gameObject.activeSelf) { port.gameObject.SetActive(false); hiddenPorts.Add(port.gameObject); }
        }
        void ShowPorts()
        {
            foreach (var port in hiddenPorts) if (port) port.SetActive(true);
            hiddenPorts.Clear();
        }
        // AR has no virtual body: the real volunteer gets no incisions or blood drawn on air.
        bool VirtualBody() => !session.presentation || !session.presentation.passthrough;
        // The outward-wound collision copy of the rendered mannequin (EnvironmentPreviewBuilder.PatientCollisionName).
        Collider PatientSkinCollider()
        {
            var mannequin = session.presentation ? session.presentation.virtualMannequin : null;
            if (!mannequin) return null;
            foreach (var hull in mannequin.GetComponentsInChildren<MeshCollider>(true)) if (hull.name == "PatientCollision") return hull;
            return null;
        }
        // State tracker facts for Jarvis (never scored): tip contact and region injuries come from the interaction.
        void Touched(InstrumentBehaviour tool, string tissueId) { if (tool && session.coach) session.coach.Contact(tool.instrumentId, tissueId); }
        void Injured(string region, InstrumentBehaviour tool, bool controlled) { if (tool && session.coach) session.coach.Injury(region, tool.instrumentId, controlled); }
        // What each hand holds, sent on change. Until the coach is paired nothing is recorded, so held tools are sent then.
        void ReportHands()
        {
            if (!session.coach || !session.coach.Connected || !session.workbench || session.workbench.inputs == null) { inHand.Clear(); return; }
            // Every put-down first, then every pick-up, so a tool passed between hands ends up held.
            for (int pass = 0; pass < 2; pass++)
                foreach (var input in session.workbench.inputs)
                {
                    if (!input || (input.controller != XRNode.LeftHand && input.controller != XRNode.RightHand)) continue;
                    var grip = input.GetComponent<InstrumentInteractor>(); var tool = grip ? grip.HeldInstrument : null;
                    inHand.TryGetValue(input.controller, out var previous);
                    if (tool == previous) continue;
                    string hand = input.controller == XRNode.LeftHand ? "left" : "right";
                    if (pass == 0) { if (previous) session.coach.Instrument(previous.instrumentId, hand, false); continue; }
                    if (tool) session.coach.Instrument(tool.instrumentId, hand, true);
                    inHand[input.controller] = tool;
                }
        }
        // Touching tissue without the trigger does nothing; say so at the tool tip, once per touch.
        void NeedsTrigger(InstrumentBehaviour tool, string verb, string tissueId)
        {
            if (!triggerHint) return;
            XRNode? hand = null;
            foreach (var input in session.workbench.inputs)
                if (input && input.GetComponent<InstrumentInteractor>()?.HeldInstrument == tool) { hand = input.controller; break; }
            triggerHint.Show(tool, verb, hand, session.workbench.headCamera ? session.workbench.headCamera.transform : null);
        }
        void Applied(BodyRecord record, InstrumentBehaviour tool)
        {
            if (triggerHint && tool) triggerHint.Hide();
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
        // The pause menu's End surgery: finish as soon as practice is running again after the menu closes.
        bool finishRequested;
        public bool FinishRequested => finishRequested;
        public void RequestFinish() => finishRequested = true;
        void OnDestroy()
        {
            if(session && session.workbench)session.workbench.ToolsReset-=ClearPlacements;
            if (interaction) { interaction.Submitted -= Applied; interaction.MarkerChanged -= Marked; interaction.Contacted -= Touched; interaction.RegionInjured -= Injured; interaction.TouchedWithoutTrigger -= NeedsTrigger; }
            if (woundFrame) { if(Application.isPlaying)Destroy(woundFrame.gameObject);else DestroyImmediate(woundFrame.gameObject); }
            if (arStand) { if(Application.isPlaying)Destroy(arStand);else DestroyImmediate(arStand); }
            // Kit tools are owned by the scene/workbench; do not leave dangling registered entries.
        }
    }
}
