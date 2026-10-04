using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Surgery
{
    // Case data supplies the question/choices. This panel emits authored decision/finish inputs;
    // it neither scores answers nor advances milestones. An empty hand and a fresh trigger edge
    // are required, so finishing a tool stroke cannot accidentally submit the case.
    [DefaultExecutionOrder(160)]
    [DisallowMultipleComponent]
    public sealed class OpenSurgeryPanel : MonoBehaviour
    {
        sealed class Button
        {
            public string choice;
            public bool finish;
            public BoxCollider collider;
            public Renderer renderer;
        }
        sealed class Hand
        {
            public XRInstrumentInput input;
            public InstrumentInteractor interactor;
            public bool pressed, requireRelease = true;
            public LineRenderer ray;
        }
        readonly List<Button> buttons = new List<Button>();
        readonly List<Hand> hands = new List<Hand>();
        readonly List<Material> materials = new List<Material>();
        OpenSurgerySession owner;
        NativeCaseSession session;
        Transform wound, panel;
        BodyDecision shownDecision;
        TextMesh recorded, prompt;
        Material buttonMaterial, hoverMaterial, finishMaterial;
        bool wasReady, showingReview;

        public void Initialize(OpenSurgerySession owner, NativeCaseSession session, Transform wound)
        {
            this.owner=owner; this.session=session; this.wound=wound;
            DisposePanel();
            if (!owner || !session || !session.workbench || !wound) return;
            var go=new GameObject("OpenSurgeryDecisionPanel"); panel=go.transform; panel.SetParent(transform,false);
            buttonMaterial=NewMaterial("SurgeryChoice",new Color(.075f,.14f,.18f));
            hoverMaterial=NewMaterial("SurgeryChoiceHover",new Color(.11f,.40f,.44f));
            finishMaterial=NewMaterial("SurgeryFinish",new Color(.13f,.22f,.28f));
            var rayMaterial=NewMaterial("SurgeryPanelRay",new Color(.28f,.9f,.94f));
            foreach(var input in session.workbench.inputs ?? Array.Empty<XRInstrumentInput>())
            {
                if(!input) continue;
                var interactor=input.GetComponent<InstrumentInteractor>(); if(!interactor) continue;
                var rayObject=new GameObject("EmptyHandPanelRay"); rayObject.transform.SetParent(panel,false);
                var ray=rayObject.AddComponent<LineRenderer>(); ray.useWorldSpace=true; ray.positionCount=2;
                ray.startWidth=.001f; ray.endWidth=.0016f; ray.sharedMaterial=rayMaterial; ray.enabled=false;
                hands.Add(new Hand{input=input,interactor=interactor,ray=ray});
            }
            RebuildDecision(); panel.gameObject.SetActive(false); wasReady=false;
        }
        Material NewMaterial(string name,Color color)
        {
            var material=TissueRuntimeMaterial.Create(name,color); material.SetFloat("_Glossiness",.05f); materials.Add(material); return material;
        }
        BodyDecision CurrentDecision()
        {
            var decisions=session?.exercise?.SelectedCase?.procedure?.openBody?.decisions;
            if(decisions==null || decisions.Length==0) return null;
            var body=session.exercise.Body;
            string tissue=owner && owner.Wound?owner.Wound.decisionTissueId:"";
            return decisions.FirstOrDefault(d=>d!=null && !(d.choices??Array.Empty<string>()).Any(c=>body!=null && body.Get(tissue,"decision_"+c)>0))
                ?? decisions.FirstOrDefault(d=>d!=null);
        }
        void RebuildDecision()
        {
            foreach(var button in buttons) if(button.collider) { button.collider.gameObject.SetActive(false); Release(button.collider.gameObject); }
            buttons.Clear();
            if(prompt) Release(prompt.gameObject); if(recorded) Release(recorded.gameObject);
            shownDecision=CurrentDecision();
            prompt=Label("DecisionPrompt",shownDecision==null?"Review this attempt":Wrap(shownDecision.prompt,37),new Vector3(-.17f,.12f,-.012f),.009f);
            string[] choices=shownDecision?.choices??Array.Empty<string>();
            float y=.055f;
            foreach(var choice in choices)
            {
                AddButton(Pretty(choice),choice,false,y); y-=.047f;
            }
            recorded=Label("RecordedDecision","",new Vector3(-.165f,y+.004f,-.012f),.0052f);
            AddButton("Premark line (judge demo)","__judge_premark",false,y-.047f);
            AddButton("Finish and review","",true,y-.094f);
            LabelOnce();
        }
        void LabelOnce()
        {
            var old=panel.Find("PanelInputHint"); if(old) Release(old.gameObject);
            Label("PanelInputHint","Empty hand: point + trigger",new Vector3(-.165f,.162f,-.012f),.005f);
        }
        TextMesh Label(string name,string text,Vector3 position,float size)
        {
            var go=new GameObject(name); go.transform.SetParent(panel,false); go.transform.localPosition=position;
            var label=go.AddComponent<TextMesh>(); label.text=text; label.fontSize=64; label.characterSize=size*14f/label.fontSize;
            label.anchor=TextAnchor.MiddleLeft; label.color=new Color(.90f,.97f,1); return label;
        }
        void AddButton(string text,string choice,bool finish,float y)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=finish?"FinishAndReview":"Choose_"+choice;
            go.transform.SetParent(panel,false); go.transform.localPosition=new Vector3(0,y,0); go.transform.localScale=new Vector3(.36f,.039f,.006f);
            var shape=go.GetComponent<BoxCollider>(); shape.isTrigger=true;
            var renderer=go.GetComponent<Renderer>(); renderer.sharedMaterial=finish?finishMaterial:buttonMaterial;
            var label=Label("ButtonLabel",Pretty(text),new Vector3(-.162f,y,-.005f),.007f);
            label.transform.SetParent(go.transform,true);
            buttons.Add(new Button{choice=choice,finish=finish,collider=shape,renderer=renderer});
        }
        void Update()
        {
            if(!panel || !owner || !session || !session.workbench) return;
            bool ready=owner.Ready && owner.Interaction && session.workbench.headCamera;
            var grade=session.exercise?session.exercise.Grade:null;
            bool review=session.exercise && session.exercise.Completed && grade!=null && session.Phase=="Recap";
            bool visible=session.RegistrationReady && session.workbench.headCamera && (ready || review);
            panel.gameObject.SetActive(visible);
            if(!visible)
            {
                foreach(var hand in hands) { hand.requireRelease=true; hand.pressed=false; hand.ray.enabled=false; }
                wasReady=false; return;
            }
            if(!wasReady) foreach(var hand in hands) hand.requireRelease=true;
            wasReady=true;
            var camera=session.workbench.headCamera.transform;
            panel.position=wound.position+camera.right*.29f+Vector3.up*.26f;
            panel.rotation=Quaternion.LookRotation(panel.position-camera.position,Vector3.up);
            if(review)
            {
                ShowReview(grade);
                foreach(var hand in hands) { hand.ray.enabled=false; hand.requireRelease=true; hand.pressed=false; }
                return;
            }
            if(showingReview) { showingReview=false; RebuildDecision(); }
            if(CurrentDecision()!=shownDecision) RebuildDecision();
            string tissue=owner.Wound?owner.Wound.decisionTissueId:"";
            var selected=(shownDecision?.choices??Array.Empty<string>()).Where(c=>session.exercise.Body.Get(tissue,"decision_"+c)>0).ToArray();
            recorded.text=selected.Length==0?"Choose when ready. You can finish anytime.":"Recorded: "+string.Join(", ",selected.Select(Pretty));
            foreach(var button in buttons)
            {
                button.renderer.sharedMaterial=button.finish?finishMaterial:buttonMaterial;
                if(button.choice=="__judge_premark")button.collider.gameObject.SetActive(owner.CanPremark);
            }
            // Panel follows registration/camera pose, so make this frame's collider transforms visible
            // before querying only its own colliders (no tissue targets receive these pointer rays).
            Physics.SyncTransforms();
            foreach(var hand in hands)
            {
                hand.ray.enabled=false;
                var device=InputDevices.GetDeviceAtXRNode(hand.input.controller);
                bool valid=hand.input.isActiveAndEnabled && hand.interactor.TrackingValid && !hand.interactor.HeldInstrument && device.isValid;
                if(!valid || !device.TryGetFeatureValue(CommonUsages.trigger,out float trigger))
                { hand.requireRelease=true; hand.pressed=false; continue; }
                if(hand.requireRelease)
                { if(trigger<.25f) hand.requireRelease=false; hand.pressed=false; continue; }
                bool pressed=trigger>=(hand.pressed ? .25f : .7f), rising=pressed&&!hand.pressed; hand.pressed=pressed;
                var ray=new Ray(hand.input.transform.position,hand.input.transform.forward);
                Button nearest=null; float distance=2;
                foreach(var button in buttons)
                    if(button.collider.Raycast(ray,out var hit,distance)) { nearest=button; distance=hit.distance; }
                if(nearest==null) continue;
                nearest.renderer.sharedMaterial=hoverMaterial;
                hand.ray.enabled=true; hand.ray.SetPosition(0,ray.origin); hand.ray.SetPosition(1,ray.GetPoint(distance));
                if(!rising) continue;
                bool accepted=nearest.finish?owner.FinishAttempt():nearest.choice=="__judge_premark"?owner.EnableJudgeFastPath():owner.ChooseBase(nearest.choice);
                if(accepted && device.TryGetHapticCapabilities(out var capability) && capability.supportsImpulse)
                    device.SendHapticImpulse(0,.15f,.04f);
                // One submission per frame; a second hand cannot submit after the attempt just ended.
                if(accepted) return;
            }
        }
        void ShowReview(BodyGrade grade)
        {
            if(!showingReview)
            {
                showingReview=true;
                foreach(var button in buttons) if(button.collider)
                { button.collider.gameObject.SetActive(false); Release(button.collider.gameObject); }
                buttons.Clear();
                if(prompt) Release(prompt.gameObject); if(recorded) Release(recorded.gameObject);
                var hint=panel.Find("PanelInputHint"); if(hint) Release(hint.gameObject);
                prompt=Label("ReviewTitle","Attempt review",new Vector3(-.17f,.12f,-.012f),.009f);
                recorded=Label("AttemptGrade","",new Vector3(-.165f,.075f,-.012f),.006f);
                recorded.anchor=TextAnchor.UpperLeft;
            }
            int met=grade.metMilestones?.Length??0, missing=grade.missingMilestones?.Length??0;
            recorded.text=string.Format(CultureInfo.InvariantCulture,
                "Illustrative score: {0:0.#}/{1}\nMilestones: {2}/{3}\nDecisions: {4}/{5}\nBlood lost: {6:0.#} ml | Active bleeds: {7:0}\nContamination: {8}\nGuardrail events: {9}\nEconomy: unscored ({10} points)\nNot a clinical proficiency assessment",
                grade.earnedPoints,grade.availablePoints,met,met+missing,grade.correctDecisions,grade.decisionCount,
                grade.bloodLostMl,grade.activeBleeds,grade.contamination?"recorded":"none recorded",
                grade.guardrailIds?.Length??0,grade.unscoredEconomyWeight);
        }
        static void Release(UnityEngine.Object value)
        {
            if(!value) return;
#if UNITY_EDITOR
            if(UnityEditor.EditorUtility.IsPersistent(value)) return;
#endif
            if(Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
        static string Pretty(string text) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase((text??"").Replace('_',' '));
        static string Wrap(string text,int columns)
        {
            var words=(text??"").Split(' '); string result=""; int count=0;
            foreach(var word in words)
            {
                if(count>0 && count+word.Length+1>columns) { result+="\n"; count=0; }
                else if(count>0) { result+=" "; count++; }
                result+=word; count+=word.Length;
            }
            return result;
        }
        void DisposePanel()
        {
            if(panel) { panel.gameObject.SetActive(false); Release(panel.gameObject); }
            panel=null; buttons.Clear(); hands.Clear(); shownDecision=null; prompt=recorded=null; showingReview=false;
            foreach(var material in materials) if(material) Release(material); materials.Clear();
        }
        void OnDisable()
        {
            if(panel) panel.gameObject.SetActive(false);
            foreach(var hand in hands) { hand.requireRelease=true; hand.pressed=false; }
            wasReady=false;
        }
        void OnDestroy() => DisposePanel();
    }
}
