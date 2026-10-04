#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Instruments;
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
            public bool HasFocus=>true;
            public bool headValid=true,rightValid=true;
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
        int checks;
        string runtimeException,endpoint;
        void Awake()
        {
            previous=XRInput.Source;source=new Input();XRInput.Source=source;
            Application.logMessageReceived+=Log;
            var config=JsonUtility.FromJson<NativeCaseSession.DevelopmentConfig>(File.ReadAllText(Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG")));
            endpoint=config.coachBaseUrl;
        }
        void Start()=>StartCoroutine(Guard(Exercise()));
        void Log(string message,string stack,LogType type)
        {
            if(type==LogType.Exception&&stack.Contains("Scalpal"))runtimeException=message;
        }
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
        IEnumerator Exercise()
        {
            session=UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Check(session&&session.enabled,"committed native scene has enabled coordinator");
            bool attemptConfirmed=false;
            Action<string> onAttempt=ignored=>attemptConfirmed=true;
            session.realtime.AttemptStarted+=onAttempt;
            yield return Wait(()=>session.workbench.IsReady&&session.Phase=="Selecting"&&session.realtime.Paired,"real Start loads case and real Update pairs headset");
            yield return Wait(()=>attemptConfirmed&&!session.realtime.AttemptPending,"real reducer confirms this headset's fresh attempt");
            session.realtime.AttemptStarted-=onAttempt;
            Check(!session.presentation.passthrough&&session.RegistrationReady&&!session.bodyRegistration.Accepted&&!session.bodyRegistration.EnabledByOperator,"VR starts ready without camera permission or body fit");
            Check(session.GetComponent<NativeProcedureInput>(),"real Start adds missing input using Unity null semantics");
            Check(session.presentation.virtualRoom.activeInHierarchy&&session.presentation.virtualMannequin.enabled&&!session.presentation.cameraManager.enabled,"VR room/mannequin visible and passthrough disabled");
            string attempt=session.realtime.AttemptId;
            yield return Button(reset:true);
            Check(session.realtime.AttemptId==attempt&&session.Phase=="Selecting","A resets tools without requesting a new attempt");
            yield return Button();
            Check(session.Phase=="Confirmed","real B edge reviews synthetic case");
            yield return Button();
            yield return Wait(()=>session.Practicing&&session.exercise.CanScore,"real B confirmation starts synchronized practice",20);
            yield return Command("highlightStructure");
            yield return Wait(()=>session.anatomy.HighlightedPartId=="appendix","real subscribed command applies actual highlight");
            yield return Command("pausePractice");
            yield return Wait(()=>!session.Practicing&&!session.anatomy.CanDisplay,"real pause command hides anatomy");
            Check(session.anatomy.HighlightedPartId=="","hidden anatomy publishes no actual highlight");
            yield return Frames(20);
            yield return StateHighlight("");
            yield return Command("resumePractice");
            yield return Wait(()=>session.Practicing&&session.anatomy.HighlightedPartId=="appendix","resume restores desired highlight");
            yield return Frames(20);yield return StateHighlight("appendix");
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
            yield return Button(reset:true);
            Check(session.realtime.AttemptId==attempt&&session.exercise.Current.id==step&&session.Practicing,"A reset in practice preserves attempt and progress");
            yield return Button(retry:true);
            yield return Wait(()=>session.realtime.AttemptId!=attempt&&session.Phase=="Selecting","left menu explicitly creates a new attempt",15);
            Check(session.anatomy.HighlightedPartId=="","new attempt clears desired highlight");
            Debug.Log("SCALPAL_NATIVE_PLAYMODE_OK checks="+checks+" realStart=true realUpdate=true realButtons=true realPhysicsTrigger=true liveLocalDb=true isolatedCoachHttp=true headsetValidated=false completeDemoFlow=false");
        }
        void MoveTip(InstrumentBehaviour tool,Vector3 target)
        {
            var hand=session.workbench.inputs.Single(i=>i.controller==XRNode.RightHand).transform;
            Vector3 world=hand.position+(target-tool.actionPoint.position);
            source.right=new Pose(session.workbench.trackingOrigin.InverseTransformPoint(world),source.right.rotation);
        }
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
                yield return request.SendWebRequest();Check(request.result==UnityWebRequest.Result.Success,"fixture command accepted: "+action);
            }
        }
        [Serializable] sealed class State {public string highlighted;}
        IEnumerator StateHighlight(string expected)
        {
            using(var request=UnityWebRequest.Get(endpoint+"/fixture/state"))
            {
                yield return request.SendWebRequest();Check(request.result==UnityWebRequest.Result.Success,"read actual reducer state");
                Check(JsonUtility.FromJson<State>(request.downloadHandler.text).highlighted==expected,"published highlight matches rendered state");
            }
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
        void OnDestroy(){Application.logMessageReceived-=Log;XRInput.Source=previous;}
    }
}
#endif
