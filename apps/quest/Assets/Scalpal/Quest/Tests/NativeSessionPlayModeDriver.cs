#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Instruments;
using Scalpal.Exercises.Coach;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR;

namespace Scalpal.Quest.Editor
{
    public sealed class NativeSessionPlayModeDriver : MonoBehaviour
    {
        sealed class Input : IXRInputSource
        {
            public bool DisplayRunning=>true;
            public bool FloorTracking=>true;
            public bool HasFocus=>focused;
            public bool headValid=true,rightValid=true,focused=true;
            public Pose head=new Pose(new Vector3(0,1.6f,0),Quaternion.identity), right=new Pose(new Vector3(.25f,1,-.3f),Quaternion.identity);
            public float grip,trigger;
            public bool primary,secondary,menu;
            public bool TryPose(XRNode node,out Pose pose){pose=node==XRNode.Head?head:node==XRNode.RightHand?right:new Pose(new Vector3(-.25f,1,-.3f),Quaternion.identity);return node==XRNode.Head?headValid:node!=XRNode.RightHand||rightValid;}
            public float Grip(XRNode node)=>node==XRNode.RightHand?grip:0;
            public float Trigger(XRNode node)=>node==XRNode.RightHand?trigger:0;
            public bool Button(XRNode node,XRInputButton button)=>node==XRNode.RightHand?(button==XRInputButton.Primary?primary:button==XRInputButton.Secondary&&secondary):node==XRNode.LeftHand&&button==XRInputButton.Menu&&menu;
        }
        Input source;
        IXRInputSource previous;
        NativeCaseSession session;
        int checks, bodyFrame, retryRequests, toolResets;
        InstrumentBehaviour resetObservedTool;
        Pose observedResetPose;
        Transform observedResetParent;
        bool resetObservationPending, resetObserved, observedToolHeld, observedHandOccupied;
        int resetEventFrame, resetObservedFrame;
        bool feedBody;
        float nextBodyFrame;
        NativePresentation syntheticPresentation;
        readonly BodySurfaceSnapshot surface = SyntheticSurface();
        string runtimeException,endpoint;
        void Awake()
        {
            previous=XRInput.Source;source=new Input();XRInput.Source=source;
            Application.logMessageReceived+=Log;
            var config=JsonUtility.FromJson<NativeCaseSession.DevelopmentConfig>(File.ReadAllText(Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG")));
            endpoint=config.coachBaseUrl;
            syntheticPresentation=UnityEngine.Object.FindFirstObjectByType<NativePresentation>();
            if(syntheticPresentation)syntheticPresentation.SyntheticCompositorReady=true;
        }
        void Start()=>StartCoroutine(Guard(Exercise()));
        void Update()
        {
            if(feedBody&&session&&Time.realtimeSinceStartup>=nextBodyFrame)ObserveBody();
        }
        void LateUpdate()
        {
            if(!resetObservationPending||!resetObservedTool)return;
            // ToolsReset is emitted before the restoring loop. LateUpdate observes
            // its result in that frame, before a subsequent FixedUpdate can settle it.
            observedResetPose=new Pose(resetObservedTool.transform.position,resetObservedTool.transform.rotation);
            observedResetParent=resetObservedTool.transform.parent;
            observedToolHeld=resetObservedTool.Held;
            observedHandOccupied=RightHand().HeldInstrument!=null;
            resetObservedFrame=Time.frameCount;
            resetObserved=true;resetObservationPending=false;
        }
        void ObserveBody()
        {
            nextBodyFrame=Time.realtimeSinceStartup+.3f;
            string id="playmode-synthetic-"+(++bodyFrame);
            var reply=SyntheticReply(id);
            // The capture age is deliberate and bounded. Depth rays and image landmarks
            // refer to the same fixture acquisition; this is not a physical camera test.
            session.bodyRegistration.Process(reply,id,new Vector2Int(640,480),Time.realtimeSinceStartup-.05f,surface);
        }
        static BodySurfaceSnapshot SyntheticSurface()
        {
            var eye=new Vector3(0,2,0);
            var result=new BodySurfaceSnapshot {
                bottomLeft=new Ray(eye,new Vector3(-1,-1,-1)),
                bottomRight=new Ray(eye,new Vector3(1,-1,-1)),
                topLeft=new Ray(eye,new Vector3(-1,-1,1)),lensForward=Vector3.down
            };
            for(int y=0;y<BodySurfaceSnapshot.Height;y++)for(int x=0;x<BodySurfaceSnapshot.Width;x++)
            {
                int i=y*BodySurfaceSnapshot.Width+x;
                result.points[i]=new Vector3(2f*x/(BodySurfaceSnapshot.Width-1)-1,1,1-2f*y/(BodySurfaceSnapshot.Height-1));
                result.normals[i]=Vector3.up;result.valid[i]=true;
            }
            return result;
        }
        static NativeBodyRegistration.Reply SyntheticReply(string id)
        {
            var landmarks=new NativeBodyRegistration.Landmark[33];
            for(int i=0;i<landmarks.Length;i++)landmarks[i]=new NativeBodyRegistration.Landmark {index=i,x=.5f,y=.5f,visibility=.9f,presence=.9f};
            int[] ids={11,12,23,24};
            var points=new[] {new Vector3(.167503f,1,.521565f),new Vector3(-.167503f,1,.521565f),
                new Vector3(.084307f,1,0),new Vector3(-.084307f,1,0)};
            for(int i=0;i<ids.Length;i++){landmarks[ids[i]].x=(points[i].x+1)/2;landmarks[ids[i]].y=(1-points[i].z)/2;}
            return new NativeBodyRegistration.Reply {schema="scalpal.body_pose.v1",frameId=id,imageWidth=640,imageHeight=480,
                coordinateConvention="normalized_image_top_left",valid=true,personCount=1,landmarks=landmarks,
                model=new NativeBodyRegistration.Model {sha256="59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a"}};
        }
        void Log(string message,string stack,LogType type)
        {
            if(type==LogType.Exception&&stack.Contains("Scalpal"))runtimeException=message;
        }
        static readonly string[] LearnerTextRoots={"ProcedureChecklistGuidance","SimulatedPatientMonitor","SceneIdentityPointer","DialogueBox","OpenSurgeryDecisionPanel","HandoffCard(Clone)"};
        void Check(bool valid,string detail)
        {
            checks++;if(!valid)throw new InvalidOperationException("Play Mode failed: "+detail);
        }
        IEnumerator Wait(Func<bool> condition,string detail,float seconds=12)
        {
            float end=Time.realtimeSinceStartup+seconds;
            while(!condition())
            {
                if(runtimeException!=null)throw new InvalidOperationException(runtimeException);
                if(Time.realtimeSinceStartup>end)throw new InvalidOperationException("Play Mode timeout: "+detail+" phase="+(session?session.Phase:"")+" message="+(session?session.Message:""));
                yield return null;
            }
            Check(true,detail);
        }
        IEnumerator Frames(int count=3){for(int i=0;i<count;i++)yield return null;}
        IEnumerator Button(bool retry=false,bool reset=false)
        {
            source.secondary=!retry&&!reset;source.menu=retry;source.primary=reset;
            yield return Frames();source.secondary=source.menu=source.primary=false;yield return Frames();
        }
        void CountRetry() => retryRequests++;
        void CountToolReset()
        {
            toolResets++;
            if(resetObservedTool)
            {
                resetEventFrame=Time.frameCount;
                resetObservationPending=true;
            }
        }
        T[] SavedWorkbenchRest<T>(string field)
        {
            var member=typeof(NativeWorkbench).GetField(field,BindingFlags.Instance|BindingFlags.NonPublic);
            if(member==null||!(member.GetValue(session.workbench) is T[] values))
                throw new InvalidOperationException("Missing workbench reset oracle: "+field);
            return values;
        }
        IEnumerator Exercise()
        {
            session=UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Check(session&&session.enabled,"committed native scene has enabled coordinator");
            session.workbench.RetryRequested+=CountRetry;
            session.workbench.ToolsReset+=CountToolReset;
            bool attemptConfirmed=false;
            Action<string> onAttempt=ignored=>attemptConfirmed=true;
            session.realtime.AttemptStarted+=onAttempt;
            yield return Wait(()=>session.workbench.IsReady&&session.Phase=="Selecting"&&session.realtime.Paired,"real Start loads case and real Update pairs headset");
            yield return Wait(()=>attemptConfirmed&&!session.realtime.AttemptPending,"real reducer confirms this headset's fresh attempt");
            session.realtime.AttemptStarted-=onAttempt;
            Check(session.presentation.passthrough&&!session.RegistrationReady&&!session.bodyRegistration.Accepted&&!session.bodyRegistration.EnabledByOperator,"MR starts with body registration and scoring gates closed");
            Check(session.GetComponent<NativeProcedureInput>(),"real Start adds missing input using Unity null semantics");
            Check(!session.presentation.virtualRoom.activeInHierarchy&&!session.presentation.virtualMannequin.enabled&&session.presentation.cameraManager.enabled,"MR hides authored room/mannequin and enables the real camera manager");
            var authoredFit=new Pose(session.bodyRegistration.anatomyFit.position,session.bodyRegistration.anatomyFit.rotation);
            Vector3 authoredFitScale=session.bodyRegistration.anatomyFit.localScale;
            var authoredFrame=new Pose(session.patientFrame.position,session.patientFrame.rotation);
            Vector3 authoredFrameScale=session.patientFrame.localScale;
            Check(!session.TrySelectOperatingRoomMode("ar",out var modeReason)&&!string.IsNullOrEmpty(modeReason),"unknown presentation mode is rejected with an explanation");
            Check(session.TrySelectOperatingRoomMode("mixed_reality",out modeReason)&&session.Phase=="Selecting","same MR mode is an accepted lifecycle-preserving no-op");
            string attempt=session.realtime.AttemptId;
            // Observe the local events: a wrongly fired RetryRequested must fail before
            // any reducer round-trip changes AttemptId. Also make the reset observable.
            var resetTool=session.workbench.tools.Single(t=>t.instrumentId=="trocar_12mm");
            // Read the Awake-captured authored reset target independently of the
            // settled physics transform; no production callback is invoked by reflection.
            int resetIndex=Array.IndexOf(session.workbench.tools,resetTool);
            var toolRest=new Pose(SavedWorkbenchRest<Vector3>("toolPositions")[resetIndex],SavedWorkbenchRest<Quaternion>("toolRotations")[resetIndex]);
            var toolRestParent=SavedWorkbenchRest<Transform>("toolParents")[resetIndex];
            yield return PickUp(resetTool.instrumentId);
            source.right.position+=new Vector3(.04f,.04f,.04f);yield return Frames();
            Check(resetTool.Held&&Vector3.Distance(resetTool.transform.position,toolRest.position)>.02f,
                "authored tool is actually held away from its rest pose before A reset");
            yield return WorkbenchTrackingLoss(resetTool,focusLoss:false);
            yield return WorkbenchTrackingLoss(resetTool,focusLoss:true);
            int retryBefore=retryRequests,resetBefore=toolResets;
            resetObservedTool=resetTool;resetObserved=false;
            yield return Button(reset:true);
            resetObservedTool=null;
            Check(retryRequests==retryBefore&&toolResets==resetBefore+1,
                "A emits one equipment reset and no immediate retry request before reducer confirmation");
            Check(resetObserved&&resetObservedFrame==resetEventFrame&&!observedToolHeld&&!observedHandOccupied&&observedResetParent==toolRestParent
                &&Vector3.Distance(observedResetPose.position,toolRest.position)<.001f&&Quaternion.Angle(observedResetPose.rotation,toolRest.rotation)<.01f,
                "A releases held tool and restores Awake-authored tray pose in the reset frame"
                +" observed="+resetObserved+" sameFrame="+(resetObservedFrame==resetEventFrame)+" held="+observedToolHeld
                +" handOccupied="+observedHandOccupied+" parentMatches="+(observedResetParent==toolRestParent)
                +" positionError="+Vector3.Distance(observedResetPose.position,toolRest.position)+" angleError="+Quaternion.Angle(observedResetPose.rotation,toolRest.rotation));
            source.grip=0;yield return Frames();
            Check(session.realtime.AttemptId==attempt&&!session.realtime.AttemptPending&&session.Phase=="Selecting","A resets tools without requesting a new attempt");
            yield return Button();
            Check(session.Phase=="Confirmed","real B edge reviews synthetic case");
            yield return Button();
            Check(session.Phase=="Confirmed"&&!session.Practicing&&!session.exercise.CanScore,"real B confirmation cannot start practice without an accepted fit");
            ObserveBody();yield return Frames();
            Check(session.bodyRegistration.CandidateValid&&!session.bodyRegistration.Accepted&&!session.RegistrationReady,"first synthetic calibrated observation cannot accept a fit");
            yield return new WaitForSecondsRealtime(.3f);ObserveBody();yield return Frames();
            Check(session.bodyRegistration.CandidateValid&&!session.bodyRegistration.Accepted&&!session.RegistrationReady,"second synthetic calibrated observation cannot accept a fit");
            yield return new WaitForSecondsRealtime(.3f);ObserveBody();yield return Frames();
            Check(session.bodyRegistration.Accepted&&session.RegistrationReady,"third synthetic calibrated observation opens real body registration gate");
            Check(Vector3.Distance(session.patientFrame.position,session.bodyRegistration.anatomyFit.TransformPoint(BodyRegistrationMath.SourceUmbilicus))<.001f,"port frame follows accepted anatomy world transform");
            // Presentation is separate from the reviewed case and shared attempt. These
            // choices use the public session entry point before either practice segment.
            string patient=session.SelectedPatientId,procedure=session.SelectedProcedureId,caseId=session.ReviewedCase.caseId;
            Check(Vector3.Distance(session.bodyRegistration.anatomyFit.position,authoredFit.position)>.01f,
                "synthetic registered fit differs from authored pose, making restoration observable");
            Check(session.TrySelectOperatingRoomMode("virtual",out modeReason),"confirmed case accepts virtual mode through the public entry point");
            yield return Frames();
            Check(session.Phase=="Confirmed"&&session.realtime.AttemptId==attempt&&session.SelectedPatientId==patient&&session.SelectedProcedureId==procedure&&session.ReviewedCase.caseId==caseId,
                "AR to VR preserves reviewed case, selected patient, lifecycle and shared attempt");
            Check(!session.bodyRegistration.Accepted&&!session.bodyRegistration.CandidateValid&&!session.bodyRegistration.EnabledByOperator,
                "leaving AR clears its accepted fit and stops body acquisition");
            CheckVirtualPresentation();
            Check(PoseMatches(session.bodyRegistration.anatomyFit,authoredFit,authoredFitScale)&&PoseMatches(session.patientFrame,authoredFrame,authoredFrameScale),
                "VR restores authored anatomy and port positions, rotations and scales after AR registration");
            Check(session.TrySelectOperatingRoomMode("mixed_reality",out modeReason),"confirmed case can return to AR before practice");
            yield return Frames();
            Check(session.Phase=="Confirmed"&&session.realtime.AttemptId==attempt&&!session.RegistrationReady&&!session.bodyRegistration.Accepted,
                "VR to AR keeps the same reviewed attempt but closes practice until a new fit");
            Check(session.presentation.passthrough&&!session.presentation.virtualRoom.activeInHierarchy&&!session.presentation.virtualMannequin.enabled&&session.presentation.cameraManager.enabled&&session.presentation.headCamera.backgroundColor.a==0,
                "AR return hides virtual patient and room and restores transparent camera");
            yield return Button();
            Check(session.Phase=="Confirmed"&&!session.Practicing&&!session.exercise.CanScore,"AR return cannot reuse the fit from before the mode change");
            ObserveBody();yield return Frames();
            Check(!session.bodyRegistration.Accepted&&session.bodyRegistration.StableObservations==1&&!session.RegistrationReady,
                "first fresh observation after roundtrip cannot reuse old stability history");
            yield return new WaitForSecondsRealtime(.3f);ObserveBody();yield return Frames();
            Check(!session.bodyRegistration.Accepted&&session.bodyRegistration.StableObservations==2&&!session.RegistrationReady,
                "second fresh observation after roundtrip keeps registration gate closed");
            yield return new WaitForSecondsRealtime(.3f);ObserveBody();yield return Frames();
            Check(session.bodyRegistration.Accepted&&session.RegistrationReady,"third fresh observation after roundtrip accepts a new fit");
            Check(session.TrySelectOperatingRoomMode("mixed_reality",out modeReason)&&session.bodyRegistration.Accepted&&session.realtime.AttemptId==attempt,
                "choosing the current AR mode preserves an accepted fit and attempt");
            feedBody=true;
            yield return Button();
            yield return Wait(()=>session.Practicing&&session.exercise.CanScore,"real B confirmation starts synchronized registered MR practice",20);
            Check(session.exercise.Body==null,"legacy port case is not routed into an empty deserialized open-body model");
            yield return Frames();
            var shownText=FindObjectsByType<TextMesh>(FindObjectsSortMode.None).Cast<Component>().Concat(FindObjectsByType<TMPro.TMP_Text>(FindObjectsSortMode.None))
                .Where(text=>text.gameObject.activeInHierarchy&&text.GetComponent<Renderer>()&&text.GetComponent<Renderer>().enabled)
                .Where(text=>!LearnerTextRoots.Any(root=>text.GetComponentsInParent<Transform>(true).Any(parent=>parent.name==root))).Select(text=>text.name).ToArray();
            Check(shownText.Length==0,"practice view shows only the checklist, Scalpal line, monitor and on-demand labels; extra world text: "+string.Join(", ",shownText));
            string mrCoachId=session.exercise.explicitCoachSessionId;
            yield return CoachMode("mixed_reality");
            Check(!session.TrySelectOperatingRoomMode("virtual",out modeReason)&&!string.IsNullOrEmpty(modeReason)&&session.Practicing&&session.presentation.passthrough&&session.realtime.AttemptId==attempt,
                "public entry point rejects a mode change during active MR practice");
            yield return Command("highlightStructure");
            yield return Wait(()=>session.anatomy.HighlightedPartId=="appendix","real subscribed command applies actual highlight");
            yield return Command("pausePractice");
            yield return Wait(()=>!session.Practicing&&!session.anatomy.CanDisplay,"real pause command hides anatomy");
            Check(session.anatomy.HighlightedPartId=="","hidden anatomy publishes no actual highlight");
            Check(!session.TrySelectOperatingRoomMode("virtual",out modeReason)&&!string.IsNullOrEmpty(modeReason)&&session.Phase=="Practicing"&&!session.Practicing&&session.presentation.passthrough,
                "paused MR practice still rejects presentation changes");
            yield return StateHighlight("");
            yield return Command("resumePractice");
            yield return Wait(()=>session.Practicing&&session.anatomy.HighlightedPartId=="appendix","resume restores desired highlight");
            yield return StateHighlight("appendix");
            var access=session.patientFrame.GetComponentsInChildren<NativePortMarker>(true).Single(p=>p.portId=="umbilical");
            yield return PickUp("trocar_12mm");
            var held=session.workbench.inputs.Single(i=>i.controller==XRNode.RightHand).GetComponent<InstrumentInteractor>().HeldInstrument;
            Vector3 frozen=held.transform.position;
            source.rightValid=false;yield return null;yield return null;
            Check(held.Held&&!held.TrackingValid&&(held.transform.position-frozen).sqrMagnitude<1e-8f,"brief real-frame loss freezes held tool and disables actions");
            source.rightValid=true;yield return Frames();
            Check(held.Held&&held.TrackingValid,"real-frame recovery retains same held tool");
            source.trigger=0;yield return Frames();
            MoveTip(held,access.transform.position);source.trigger=1;
            yield return Wait(()=>session.exercise.Current?.id=="working_ports","actual FixedUpdate OnTriggerStay places first authored port",12);
            source.trigger=0;source.grip=0;yield return Frames();
            Check(session.exercise.Current?.id=="working_ports","physics contact advanced actual authored runner");
            // A reset in active practice must preserve the live attempt and completed port.
            attempt=session.realtime.AttemptId;string step=session.exercise.Current.id;
            retryBefore=retryRequests;resetBefore=toolResets;
            yield return Button(reset:true);
            Check(retryRequests==retryBefore&&toolResets==resetBefore+1,
                "MR practice A resets equipment without an immediate retry event");
            Check(session.realtime.AttemptId==attempt&&session.exercise.Current.id==step&&session.Practicing,"A reset in practice preserves attempt and progress");
            yield return Button(retry:true);
            Check(retryRequests==retryBefore+1,"left menu emits the explicit retry event through real Update");
            yield return Wait(()=>session.realtime.AttemptId!=attempt&&session.Phase=="Selecting","left menu explicitly creates a new attempt",15);
            Check(session.anatomy.HighlightedPartId=="","new attempt clears desired highlight");
            feedBody=false;
            yield return Wait(()=>!session.realtime.AttemptPending,"new explicit retry is committed before choosing VR");
            attempt=session.realtime.AttemptId;
            Check(session.TrySelectOperatingRoomMode("virtual",out modeReason),"selection chooses VR through the same shared session entry point");
            yield return Frames();
            Check(session.Phase=="Selecting"&&session.realtime.AttemptId==attempt&&session.SelectedPatientId==patient&&session.SelectedProcedureId==procedure,
                "VR selection preserves lifecycle and selected case without another attempt");
            CheckVirtualPresentation();
            Check(!session.bodyRegistration.Accepted&&!session.bodyRegistration.CandidateValid&&!session.bodyRegistration.EnabledByOperator,
                "VR practice does not acquire or consume a participant fit");
            Check(PoseMatches(session.bodyRegistration.anatomyFit,authoredFit,authoredFitScale)&&PoseMatches(session.patientFrame,authoredFrame,authoredFrameScale),
                "later VR run again restores authored transforms rather than the latest AR fit");
            yield return Button();Check(session.Phase=="Confirmed","real B edge reviews the same case in VR");
            yield return Button();
            yield return Wait(()=>session.Practicing&&session.exercise.CanScore,"real B confirmation starts synchronized VR practice without a body fit",20);
            Check(!session.bodyRegistration.Accepted&&session.RegistrationReady&&session.patientFrame.gameObject.activeInHierarchy,
                "VR practice exposes authored ports while body registration remains absent");
            Check(session.exercise.explicitCoachSessionId!=mrCoachId,"VR run binds a fresh coach instead of reusing the MR session");
            yield return CoachMode("virtual");
            Check(!session.TrySelectOperatingRoomMode("mixed_reality",out modeReason)&&!string.IsNullOrEmpty(modeReason)&&session.Practicing&&!session.presentation.passthrough,
                "active VR practice rejects a mid-procedure switch to AR");
            access=session.patientFrame.GetComponentsInChildren<NativePortMarker>(true).Single(p=>p.portId=="umbilical");
            yield return PickUp("trocar_12mm");
            held=session.workbench.inputs.Single(i=>i.controller==XRNode.RightHand).GetComponent<InstrumentInteractor>().HeldInstrument;
            MoveTip(held,access.transform.position);source.trigger=1;
            yield return Wait(()=>session.exercise.Current?.id=="working_ports","VR actual FixedUpdate OnTriggerStay places the same authored port",12);
            source.trigger=0;source.grip=0;yield return Frames();
            Check(session.exercise.Current?.id=="working_ports"&&session.Practicing,"VR uses the same authored exercise core and actual tool physics");
            retryBefore=retryRequests;resetBefore=toolResets;
            yield return Button(reset:true);
            Check(retryRequests==retryBefore&&toolResets==resetBefore+1,
                "VR practice A resets equipment without an immediate retry event");
            Check(session.realtime.AttemptId==attempt&&session.exercise.Current?.id=="working_ports"&&session.Practicing,
                "VR A reset also preserves the shared attempt and authored progress");
            Debug.Log("SCALPAL_NATIVE_PLAYMODE_OK checks="+checks+" realStart=true realUpdate=true realButtons=true realPhysicsTrigger=true liveLocalDb=true isolatedCoachHttp=true operatingRoomModes=mixed_reality,virtual syntheticCompositor=true syntheticBodyFrames=true headsetValidated=false completeDemoFlow=false");
        }
        static bool PoseMatches(Transform target,Pose expected,Vector3 scale) =>
            Vector3.Distance(target.position,expected.position)<.001f&&Quaternion.Angle(target.rotation,expected.rotation)<.01f&&(target.localScale-scale).sqrMagnitude<1e-8f;
        void CheckVirtualPresentation()
        {
            Check(!session.presentation.passthrough&&session.RegistrationReady&&session.presentation.virtualRoom.activeInHierarchy&&session.presentation.virtualMannequin.enabled
                &&!session.presentation.cameraManager.enabled&&session.presentation.headCamera.backgroundColor.a==1,
                "VR has an authored room and patient, opaque camera and no camera-registration readiness dependency");
        }
        IEnumerator CoachMode(string expected)
        {
            string id=session.exercise.explicitCoachSessionId;
            Check(!string.IsNullOrEmpty(id),"practice bound an explicit isolated coach session");
            using(var request=UnityWebRequest.Get(endpoint+"/coach/sessions/"+Uri.EscapeDataString(id)))
            {
                yield return request.SendWebRequest();Check(request.result==UnityWebRequest.Result.Success,"read actual live coach HTTP session");
                var state=JsonUtility.FromJson<CoachSessionState>(request.downloadHandler.text);
                Check(state?.snapshot!=null&&state.snapshot.mode==expected&&state.snapshot.patientId==session.SelectedPatientId&&state.snapshot.procedureId==session.SelectedProcedureId,
                    "coach service holds the requested "+expected+" presentation for the selected case");
            }
        }
        void MoveTip(InstrumentBehaviour tool,Vector3 target)
        {
            var hand=session.workbench.inputs.Single(i=>i.controller==XRNode.RightHand).transform;
            Vector3 world=hand.position+(target-tool.actionPoint.position);
            source.right=new Pose(session.workbench.trackingOrigin.InverseTransformPoint(world),source.right.rotation);
        }
        IEnumerator WorkbenchTrackingLoss(InstrumentBehaviour held,bool focusLoss)
        {
            var hand=RightHand();
            float grace=hand.trackingGraceSeconds;
            // Leave a generous grace for the frame-path assertions on slow Editor
            // machines. The separate right-hand check still uses the runtime default.
            hand.trackingGraceSeconds=2;
            Vector3 frozen=held.transform.position;
            try
            {
                if(focusLoss)source.focused=false;else source.headValid=false;
                yield return Frames(2);
                Check(!session.workbench.IsReady&&session.workbench.inputs.All(input=>!input.enabled)
                    &&held.Held&&!held.TrackingValid&&(held.transform.position-frozen).sqrMagnitude<1e-8f,
                    (focusLoss?"focus":"head tracking")+" loss gates real-frame inputs and freezes held tool");
                source.focused=source.headValid=true;
                yield return Frames();
                Check(session.workbench.IsReady&&session.workbench.inputs.All(input=>input.enabled)&&held.Held&&held.TrackingValid,
                    (focusLoss?"focus":"head tracking")+" recovery re-enables inputs and retains the same tool");
            }
            finally
            {
                source.focused=source.headValid=true;
                hand.trackingGraceSeconds=grace;
            }
        }
        InstrumentInteractor RightHand() => session.workbench.inputs.Single(i=>i.controller==XRNode.RightHand).GetComponent<InstrumentInteractor>();
        IEnumerator PickUp(string id)
        {
            var tool=session.workbench.tools.Single(t=>t.instrumentId==id);
            source.grip=source.trigger=0;
            source.right=new Pose(session.workbench.trackingOrigin.InverseTransformPoint(tool.transform.position),Quaternion.identity);
            yield return Frames();source.grip=1;
            yield return Wait(()=>session.workbench.inputs.Single(i=>i.controller==XRNode.RightHand).GetComponent<InstrumentInteractor>().HeldInstrument==tool,"real grip input picks up authored tool");
        }
        IEnumerator Command(string action)
        {
            using(var request=new UnityWebRequest(endpoint+"/fixture/command/"+action,"POST"))
            {
                request.downloadHandler=new DownloadHandlerBuffer();request.timeout=8;
                yield return request.SendWebRequest();Check(request.result==UnityWebRequest.Result.Success,"fixture command accepted: "+action+" HTTP="+request.responseCode+" outcome="+request.downloadHandler.text);
            }
        }
        [Serializable] sealed class State {public string highlighted;}
        IEnumerator StateHighlight(string expected)
        {
            // Publish is throttled by elapsed time, so an uncapped frame count cannot
            // establish that the subscribed reducer state has caught up after resume.
            float deadline=Time.realtimeSinceStartup+12;
            string observed=null;
            do
            {
                if(runtimeException!=null)throw new InvalidOperationException(runtimeException);
                using(var request=UnityWebRequest.Get(endpoint+"/fixture/state"))
                {
                    request.timeout=3;
                    yield return request.SendWebRequest();
                    Check(request.result==UnityWebRequest.Result.Success,"read actual reducer state");
                    var state=JsonUtility.FromJson<State>(request.downloadHandler.text);
                    Check(state!=null,"fixture returns a reducer state");
                    observed=state.highlighted;
                    if(observed==expected)
                    {
                        Check(true,"published highlight matches rendered state");
                        yield break;
                    }
                }
                yield return new WaitForSecondsRealtime(.05f);
            } while(Time.realtimeSinceStartup<deadline);
            throw new InvalidOperationException("Play Mode timeout: published highlight expected="+expected+" actual="+observed);
        }
        IEnumerator Guard(IEnumerator work)
        {
            var stack=new Stack<IEnumerator>();stack.Push(work);bool failed=false;
            while(stack.Count>0)
            {
                bool moved=false;object current=null;
                try{moved=stack.Peek().MoveNext();if(moved)current=stack.Peek().Current;}
                catch(Exception error){Debug.LogError("SCALPAL_NATIVE_PLAYMODE_FAILED "+error.Message);failed=true;break;}
                if(!moved){(stack.Pop() as IDisposable)?.Dispose();continue;}
                if(current is IEnumerator nested){stack.Push(nested);continue;}
                yield return current;
            }
            while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();
            SessionState.SetBool("Scalpal.NativePlayMode.Passed",!failed);EditorApplication.ExitPlaymode();
        }
        void OnDestroy()
        {
            if(session&&session.workbench)
            {
                session.workbench.RetryRequested-=CountRetry;
                session.workbench.ToolsReset-=CountToolReset;
            }
            Application.logMessageReceived-=Log;XRInput.Source=previous;
            if(syntheticPresentation)syntheticPresentation.SyntheticCompositorReady=false;
        }
    }
}
#endif
