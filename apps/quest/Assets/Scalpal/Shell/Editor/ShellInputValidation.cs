using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Scalpal.Shell.Editor
{
    // Actual production actions against Unity's HandInteraction layout. Synthetic events do not
    // establish Quest tracking, pose accuracy, runtime extension support or physical usability.
    public static class ShellInputValidation
    {
        static void UpdateInput()
        {
            // Editor updates deliberately skip action state changes. Pump a player Dynamic update
            // through the package overload while keeping the actual production actions/events.
            typeof(InputSystem).GetMethod("Update",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(InputUpdateType)},null)
                .Invoke(null,new object[]{InputUpdateType.Dynamic});
        }
        public static int Run()
        {
            int checks=0;
            Action<bool,string> check=(ok,message)=>{ if(!ok) throw new InvalidOperationException("Shell input: "+message); checks++; };
            // Batch -executeMethod can precede OpenXRInput's first Editor update, which normally
            // registers its Pose type. Match the compiled profile's exact type, then restore it.
            var priorPose=InputSystem.LoadLayout("Pose");
            var poseJson=priorPose?.ToJson();
            var expectedPose=typeof(HandInteractionProfile.HandInteraction).GetProperty("devicePose").PropertyType;
            bool changedPose=priorPose==null || priorPose.type!=expectedPose;
            bool ownLayout=!InputSystem.ListLayouts().Contains("HandInteraction");
            const string editorInputFlag="RUN_PLAYER_UPDATES_IN_EDIT_MODE";
            bool priorPlayerUpdates=(bool)typeof(InputSettings).GetMethod("IsFeatureEnabled",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(InputSystem.settings,new object[]{editorInputFlag});
            HandInteractionProfile.HandInteraction hand=null;
            GameObject owner=null;
            ShellInput input=null;
            const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
            try
            {
                InputSystem.settings.SetInternalFeatureFlag(editorInputFlag,true);
                if(changedPose) InputSystem.RegisterLayout(expectedPose,"Pose");
                if(ownLayout) InputSystem.RegisterLayout<HandInteractionProfile.HandInteraction>("HandInteraction");
                hand=(HandInteractionProfile.HandInteraction)InputSystem.AddDevice("HandInteraction");
                InputSystem.SetDeviceUsage(hand,UnityEngine.InputSystem.CommonUsages.LeftHand);
                owner=new GameObject("ShellSyntheticHandValidation"); owner.SetActive(false);
                input=owner.AddComponent<ShellInput>();
                typeof(ShellInput).GetMethod("OnEnable",Private).Invoke(input,null);
                var pointers=(Array)typeof(ShellInput).GetField("pointers",Private).GetValue(input);
                var left=pointers.GetValue(0); var right=pointers.GetValue(1);
                Func<object,string,InputAction> action=(p,name)=>(InputAction)p.GetType().GetField(name).GetValue(p);
                var position=action(left,"position"); var rotation=action(left,"rotation");
                var tracked=action(left,"tracked"); var pinch=action(left,"pinch"); var ready=action(left,"pinchReady");
                check(position.controls.Contains(hand.pointerPosition),"production left position resolves actual OpenXR control");
                check(rotation.controls.Contains(hand.pointerRotation),"production left rotation resolves actual OpenXR control");
                check(tracked.controls.Contains(hand.pointer.isTracked),"tracking gate binds pointer pose confidence");
                check(ready.controls.Contains(hand.pinchReady),"pinch readiness binds actual hand control");
                check(action(right,"position").controls.Count==0,"left-hand usage cannot populate right-hand action");
                var expectedPosition=new Vector3(.01f,.02f,-.03f);
                var expectedRotation=Quaternion.Euler(7,28,-4);
                // These raw deltas use Unity's real control storage. Boolean readiness may be a
                // packed bitfield; StateEvent writes that without inventing a test-only layout.
                InputSystem.QueueDeltaStateEvent(hand.pointerPosition,expectedPosition);
                InputSystem.QueueDeltaStateEvent(hand.pointerRotation,expectedRotation);
                InputSystem.QueueDeltaStateEvent(hand.pinchValue,.31f);
                UpdateInput();
                using(StateEvent.From(hand,out var state))
                {
                    hand.pointer.isTracked.WriteValueIntoEvent(1f,state);
                    hand.pinchReady.WriteValueIntoEvent(1f,state);
                    InputSystem.QueueEvent(state);
                }
                UpdateInput();
                check(Vector3.Distance(position.ReadValue<Vector3>(),expectedPosition)<.00001f,"sub-button-threshold position is preserved in meters; action="+position.ReadValue<Vector3>()+" control="+hand.pointerPosition.ReadValue()+" phase="+position.phase+" type="+position.type);
                check(Quaternion.Angle(rotation.ReadValue<Quaternion>(),expectedRotation)<.01f,"quaternion pose survives production action reading");
                check(tracked.ReadValue<float>()>.5f && ready.ReadValue<float>()>.5f,"tracked ready hand opens validity inputs");
                check(Mathf.Abs(pinch.ReadValue<float>()-.31f)<.00001f,"analog pinch is not reduced to a button");
                InputSystem.QueueDeltaStateEvent(hand.pinchValue,.9f); UpdateInput();
                check(pinch.ReadValue<float>()>.75f,"pinch crossing the production threshold is readable");
                using(StateEvent.From(hand,out var lossState))
                {
                    hand.pointer.isTracked.WriteValueIntoEvent(0f,lossState);
                    hand.pinchReady.WriteValueIntoEvent(0f,lossState);
                    InputSystem.QueueEvent(lossState);
                }
                InputSystem.QueueDeltaStateEvent(hand.pinchValue,0f); UpdateInput();
                check(tracked.ReadValue<float>()==0 && ready.ReadValue<float>()==0 && pinch.ReadValue<float>()==0,"tracking loss and pinch release reach production action inputs");
                check(ShellInput.CanPoint(false,false,false),"normal hub input permitted");
                check(!ShellInput.CanPoint(true,false,false),"black transition blocks normal hub input");
                check(!ShellInput.CanPoint(true,true,false),"paused transition blocks background input");
                check(ShellInput.CanPoint(true,true,true),"paused transition permits Resume menu input");
                check(!ShellInput.CanPoint(false,false,true),"stale pause root cannot accept input after resume");
                Debug.Log("SCALPAL_SHELL_INPUT_VERIFY_OK checks="+checks+" actualOpenXRLayout=true syntheticEvents=true headset=false");
                return checks;
            }
            finally
            {
                if(input) typeof(ShellInput).GetMethod("OnDisable",Private).Invoke(input,null);
                if(owner) UnityEngine.Object.DestroyImmediate(owner);
                if(hand!=null && hand.added) InputSystem.RemoveDevice(hand);
                if(ownLayout) InputSystem.RemoveLayout("HandInteraction");
                if(changedPose) { if(poseJson!=null) InputSystem.RegisterLayout(poseJson); else InputSystem.RemoveLayout("Pose"); }
                InputSystem.settings.SetInternalFeatureFlag(editorInputFlag,priorPlayerUpdates);
            }
        }
    }
}
