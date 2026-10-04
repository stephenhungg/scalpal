using System;
using System.Linq;
using System.Reflection;
using Scalpal.Brand;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Scalpal.Shell.Editor
{
    // Production pointer source (ScalpalAim) against Unity's real OpenXR OculusTouchController and
    // HandInteraction layouts. Synthetic events do not establish Quest tracking, pose accuracy,
    // runtime extension support or physical usability.
    public static class ShellInputValidation
    {
        static void UpdateInput()
        {
            // Editor updates deliberately skip action state changes. Pump a player Dynamic update
            // through the package overload while keeping the actual production controls/events.
            typeof(InputSystem).GetMethod("Update",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(InputUpdateType)},null)
                .Invoke(null,new object[]{InputUpdateType.Dynamic});
        }
        static void Tracked(InputDevice device,UnityEngine.InputSystem.Controls.ButtonControl tracked,float value,UnityEngine.InputSystem.Controls.ButtonControl second=null)
        {
            using(StateEvent.From(device,out var state))
            {
                tracked.WriteValueIntoEvent(value,state);
                second?.WriteValueIntoEvent(value,state);
                InputSystem.QueueEvent(state);
            }
            UpdateInput();
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
            bool ownHandLayout=!InputSystem.ListLayouts().Contains("HandInteraction");
            // OpenXR registers its controller layouts only once the runtime starts, so the Editor cannot
            // instantiate OculusTouchController. Mirror its aim/grip/trigger control names on an XRController
            // and prove (below, by reflection) that every Quest profile exposes exactly those controls.
            const string touchLayout="ScalpalValidationTouchController";
            const string editorInputFlag="RUN_PLAYER_UPDATES_IN_EDIT_MODE";
            bool priorPlayerUpdates=(bool)typeof(InputSettings).GetMethod("IsFeatureEnabled",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(InputSystem.settings,new object[]{editorInputFlag});
            HandInteractionProfile.HandInteraction hand=null;
            UnityEngine.InputSystem.XR.XRController controller=null;
            try
            {
                InputSystem.settings.SetInternalFeatureFlag(editorInputFlag,true);
                if(changedPose) InputSystem.RegisterLayout(expectedPose,"Pose");
                if(ownHandLayout) InputSystem.RegisterLayout<HandInteractionProfile.HandInteraction>("HandInteraction");
                InputSystem.RegisterLayout("{\"name\":\""+touchLayout+"\",\"extend\":\"XRController\",\"controls\":["+
                    "{\"name\":\"pointerPosition\",\"layout\":\"Vector3\"},{\"name\":\"pointerRotation\",\"layout\":\"Quaternion\"},{\"name\":\"trigger\",\"layout\":\"Axis\"}]}");
                foreach(var profile in new[]{typeof(OculusTouchControllerProfile.OculusTouchController),typeof(MetaQuestTouchPlusControllerProfile.QuestTouchPlusController),typeof(MetaQuestTouchProControllerProfile.QuestProTouchController)})
                {
                    Func<string,Type> control=name=>profile.GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly)?.PropertyType;
                    check(control("pointerPosition")==typeof(UnityEngine.InputSystem.Controls.Vector3Control) && control("pointerRotation")==typeof(UnityEngine.InputSystem.Controls.QuaternionControl)
                        && control("isTracked")==typeof(UnityEngine.InputSystem.Controls.ButtonControl) && typeof(UnityEngine.InputSystem.Controls.AxisControl).IsAssignableFrom(control("trigger")) && typeof(UnityEngine.InputSystem.XR.XRController).IsAssignableFrom(profile),
                        "OpenXR "+profile.Name+" exposes the aim pose (pointerPosition/Rotation), isTracked and trigger that ScalpalAim reads");
                }
                check(ScalpalAim.Override==null,"no simulated aim source is installed outside validation");

                // Controller: the ray must use the aim pose (pointerPosition/Rotation), never the grip pose.
                controller=(UnityEngine.InputSystem.XR.XRController)InputSystem.AddDevice(touchLayout);
                var pointerPosition=controller.GetChildControl<UnityEngine.InputSystem.Controls.Vector3Control>("pointerPosition");
                var pointerRotation=controller.GetChildControl<UnityEngine.InputSystem.Controls.QuaternionControl>("pointerRotation");
                var trigger=controller.GetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("trigger");
                InputSystem.SetDeviceUsage(controller,UnityEngine.InputSystem.CommonUsages.LeftHand);
                var grip=new Vector3(-.18f,1.02f,.22f); var gripRotation=Quaternion.Euler(55,-8,4);
                var aim=new Vector3(-.17f,1.09f,.30f); var aimRotation=Quaternion.Euler(12,-6,2);
                InputSystem.QueueDeltaStateEvent(controller.devicePosition,grip);
                InputSystem.QueueDeltaStateEvent(controller.deviceRotation,gripRotation);
                InputSystem.QueueDeltaStateEvent(pointerPosition,aim);
                InputSystem.QueueDeltaStateEvent(pointerRotation,aimRotation);
                InputSystem.QueueDeltaStateEvent(trigger,.2f);
                UpdateInput();
                check(ScalpalAim.Read(0).kind==ScalpalPointerKind.None,"an untracked controller produces no ray");
                Tracked(controller,controller.isTracked,1);
                var sample=ScalpalAim.Read(0);
                check(sample.kind==ScalpalPointerKind.Controller && sample.device==controller,"tracked Touch controller is the left pointer source");
                check(Vector3.Distance(sample.position,aim)<1e-5f && Vector3.Distance(sample.position,grip)>.05f,"ray origin is the OpenXR aim position, not the grip position; got "+sample.position);
                check(Quaternion.Angle(sample.rotation,aimRotation)<.01f && Quaternion.Angle(sample.rotation,gripRotation)>30,"ray direction is the aim forward, not the grip forward");
                check(Mathf.Abs(sample.select-.2f)<1e-5f,"analog trigger is the select value");
                check(ScalpalAim.Read(1).kind==ScalpalPointerKind.None,"left-hand usage cannot populate the right pointer");
                InputSystem.QueueDeltaStateEvent(pointerPosition,Vector3.zero);
                InputSystem.QueueDeltaStateEvent(pointerRotation,new Quaternion(0,0,0,0));
                UpdateInput();
                check(ScalpalAim.Read(0).kind==ScalpalPointerKind.None,"an unpopulated (zero) aim pose never draws a ray from the floor origin");
                InputSystem.QueueDeltaStateEvent(pointerPosition,aim);
                InputSystem.QueueDeltaStateEvent(pointerRotation,aimRotation);
                UpdateInput();

                // Hands: the hand-interaction pointer pose is used only when no controller is tracked for that hand.
                hand=(HandInteractionProfile.HandInteraction)InputSystem.AddDevice("HandInteraction");
                InputSystem.SetDeviceUsage(hand,UnityEngine.InputSystem.CommonUsages.LeftHand);
                var expectedPosition=new Vector3(.01f,.02f,-.03f);
                var expectedRotation=Quaternion.Euler(7,28,-4);
                InputSystem.QueueDeltaStateEvent(hand.pointerPosition,expectedPosition);
                InputSystem.QueueDeltaStateEvent(hand.pointerRotation,expectedRotation);
                InputSystem.QueueDeltaStateEvent(hand.pinchValue,.31f);
                UpdateInput();
                Tracked(hand,hand.pointer.isTracked,1,hand.pinchReady);
                check(ScalpalAim.Read(0).kind==ScalpalPointerKind.Controller,"a tracked controller wins over hand interaction for the same hand");
                Tracked(controller,controller.isTracked,0);
                sample=ScalpalAim.Read(0);
                check(sample.kind==ScalpalPointerKind.Hand && Vector3.Distance(sample.position,expectedPosition)<.00001f,"untracked controller hands the pointer to the hand-interaction pointer pose; got "+sample.position);
                check(Quaternion.Angle(sample.rotation,expectedRotation)<.01f,"hand pointer rotation survives production reading");
                check(Mathf.Abs(sample.select-.31f)<.00001f,"analog pinch is not reduced to a button");
                InputSystem.QueueDeltaStateEvent(hand.pinchValue,.9f); UpdateInput();
                check(ScalpalAim.Read(0).select>.75f,"pinch crossing the production threshold is readable");
                Tracked(hand,hand.pointer.isTracked,0,hand.pinchReady);
                check(ScalpalAim.Read(0).kind==ScalpalPointerKind.None,"tracking loss on every source hides the pointer");

                var pressGate=new ScalpalPressGate();
                Func<float,float,bool> press=(value,now)=>pressGate.Sample(value,true,now);
                check(!press(.9f,0),"initial held select cannot press before a release");
                check(!press(.2f,.1f) && press(.8f,1f),"released select gets one deliberate press above .75");
                check(!press(.74f,1.01f) && !press(.76f,1.02f) && !press(.6f,1.03f) && !press(.9f,1.04f),"threshold jitter cannot repeat a held press");
                check(!press(.44f,1.05f) && !press(.8f,1.10f),"release below .45 does not bypass 150 ms press interval");
                check(!press(.9f,1.30f),"suppressed rapid press never fires late while still held");
                check(!press(.2f,1.31f) && press(.9f,1.32f),"new release and press after cooldown selects exactly once");
                check(!pressGate.Sample(.9f,false,1.4f) && !press(.9f,1.6f),"tracking loss requires a new release even after cooldown");
                check(!press(.2f,1.7f) && press(.9f,1.8f),"tracking recovery accepts only a fresh deliberate press");
                check(!pressGate.Sample(float.NaN,true,2f),"invalid select values disarm selection");
                check(ShellInput.CanPoint(false,false,false),"normal hub input permitted");
                check(!ShellInput.CanPoint(true,false,false),"black transition blocks normal hub input");
                check(!ShellInput.CanPoint(true,true,false),"paused transition blocks background input");
                check(ShellInput.CanPoint(true,true,true),"paused transition permits Resume menu input");
                check(!ShellInput.CanPoint(false,false,true),"stale pause root cannot accept input after resume");
                Debug.Log("SCALPAL_SHELL_INPUT_VERIFY_OK checks="+checks+" aimPose=true touchProfiles=reflected handInteractionLayout=actual syntheticEvents=true headset=false");
                return checks;
            }
            finally
            {
                if(hand!=null && hand.added) InputSystem.RemoveDevice(hand);
                if(controller!=null && controller.added) InputSystem.RemoveDevice(controller);
                if(ownHandLayout) InputSystem.RemoveLayout("HandInteraction");
                InputSystem.RemoveLayout(touchLayout);
                if(changedPose) { if(poseJson!=null) InputSystem.RegisterLayout(poseJson); else InputSystem.RemoveLayout("Pose"); }
                InputSystem.settings.SetInternalFeatureFlag(editorInputFlag,priorPlayerUpdates);
            }
        }
    }
}
