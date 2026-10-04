using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Scalpal.Brand;
using Scalpal.Brand.Editor;
using Scalpal.Voice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.EncounterOffice.Editor
{
    public static class EncounterOfficeValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        [Serializable] sealed class VoiceInit { public Variables dynamic_variables; public Config conversation_config_override; }
        [Serializable] sealed class Variables { public string encounter_id, session_id, coach_session_id, patient_id, mode; }
        [Serializable] sealed class Config { public Agent agent; public Tts tts; }
        [Serializable] sealed class Agent { public Prompt prompt; public string first_message; }
        [Serializable] sealed class Prompt { public string prompt; }
        [Serializable] sealed class Tts { public string voice_id; }
        [Serializable] sealed class SpeechManifest { public string speakerRole; public SpeechEntry[] entries; }
        [Serializable] sealed class SpeechEntry { public string patientId,tool,argument,exactDisplayText,resourcePath; }
        public static void Run()
        {
            checks=0;
            ValidateScene(); ValidateContracts(); ValidateExchange();
            UnityEngine.Debug.Log("SCALPAL_ENCOUNTER_OFFICE_VERIFY_OK checks="+checks+" actualCoroutineHttp=true provider=false headset=false");
        }
        static void ValidateScene()
        {
            var before = EditorBuildSettings.scenes.Select(scene=>scene.path+scene.enabled).ToArray();
            var scene = EditorSceneManager.OpenScene(EncounterOfficeBuild.ScenePath,OpenSceneMode.Single);
            var session=UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
            var rig=UnityEngine.Object.FindFirstObjectByType<EncounterOfficeRig>();
            var panel=UnityEngine.Object.FindFirstObjectByType<EncounterOfficePanel>();
            Check(session&&session.voice&&session.patient&&rig&&rig.origin&&rig.head&&rig.left&&rig.right,"dedicated scene binds encounter, voice, patients and tracked rig");
            Check(session.realtime&&!session.realtime.autoConnect&&session.realtime.gameObject==session.gameObject&&session.State==null,"office binds pairing bridge without automatic networking or automatic case selection");
            Check(rig.head.transform.parent==rig.origin&&rig.left.parent==rig.origin&&rig.right.parent==rig.origin,"head and controllers share one floor tracking origin");
            Check(rig.session==session&&rig.talkHint&&panel,"tracked hold-to-answer rig and first-use hint bind the interview");
            Check(panel&&panel.session==session&&panel.options.Length==4&&panel.chart,"office panel has a paged patient picker and a findings/scorecard panel");
            Check(session.patient.female&&session.patient.male&&!session.patient.female.activeSelf&&!session.patient.male.activeSelf,"both generic adult presentations bind and remain hidden until authoritative demographics arrive");
            foreach(var patient in new[]{session.patient.female,session.patient.male})
                Check(patient.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="HeadPivot")&&patient.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="JawPivot"),"licensed weighted human head and jaw animation nodes exist");
            foreach(var patient in new[]{session.patient.female,session.patient.male})
            {
                var nose=patient.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="NoseTip");
                var headPivot=patient.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="HeadPivot");
                Check(nose.position.z-headPivot.position.z>.02f,"imported seated character faces clinician at Unity positive Z");
            }
            foreach(var patient in new[]{session.patient.female,session.patient.male})
            {
                var skins=patient.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Check(skins.Length==6,"each licensed adult human imports six skinned meshes");
                Check(skins.All(skin=>skin.sharedMesh&&skin.bones.Length>0&&skin.sharedMesh.boneWeights.Length==skin.sharedMesh.vertexCount),"human meshes retain actual donor skin weights and bones");
                var headBone=patient.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="HeadPivot");
                var jawBone=patient.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="JawPivot");
                Check(WeightedBone(skins,headBone)&&WeightedBone(skins,jawBone),"speaking/listening transforms are weighted human head and jaw bones rather than empty decoration");
                Check(skins.SelectMany(skin=>skin.sharedMaterials).Any(material=>material.name.Contains("Skin")&&material.mainTexture&&AssetDatabase.GetAssetPath(material.mainTexture).StartsWith(EncounterOfficeBuild.Root+"/Art/Textures/",StringComparison.Ordinal)),"natural skin material binds committed donor texture");
                Check(skins.SelectMany(skin=>skin.sharedMaterials).Where(material=>material.mainTexture).Select(material=>material.mainTexture).Distinct().Count()>=3,"human skin, hair and clothing preserve textured appearance");
                float nearestPatientZ=patient.GetComponentsInChildren<Renderer>(true).Max(renderer=>renderer.bounds.max.z);
                Check(panel.assessment.position.z>nearestPatientZ+.05f&&panel.keyboard.position.z>nearestPatientZ+.05f,"assessment and keyboard stay physically in front of imported patient bounds with 5cm clearance: "+patient.name);
            }
            foreach(var root in scene.GetRootGameObjects())
            {
                Check(!root.GetComponentsInChildren<Component>(true).Any(component=>component==null),"scene has no missing scripts");
                Check(!root.GetComponentsInChildren<Renderer>(true).Any(renderer=>renderer.sharedMaterials.Any(material=>!material||!material.shader)),"scene renderers have supported materials");
            }
            ValidatePresentationDemographics(session.patient);
            ValidatePatientAnimation(session);
            ValidateArmAnimation(session.patient);
            ValidateSpeechAndTalk(session.patient);
            Check(!UnityEngine.Object.FindFirstObjectByType<Scalpal.Quest.NativeCaseSession>()&&!UnityEngine.Object.FindFirstObjectByType<Scalpal.Instruments.TrainingTarget>(),"encounter scene does not instantiate surgery progression or scored tissue targets");
            var buttons=UnityEngine.Object.FindObjectsByType<EncounterOfficeButton>(FindObjectsInactive.Include,FindObjectsSortMode.None);
            foreach(var command in new[]{"reload_patients","page","option"})
                Check(buttons.Any(button=>button.command==command&&button.GetComponent<Collider>()&&button.panel==panel),"ray-accessible picker control exists: "+command);
            panel.Refresh();
            // Without a patient the room shows only the picker: patient list, paging and reload.
            Check(panel.PickerConsole.gameObject.activeSelf&&!panel.FindingsConsole.gameObject.activeSelf&&!panel.assessment.gameObject.activeSelf&&!panel.keyboard.gameObject.activeSelf
                &&buttons.Where(button=>button.gameObject.activeInHierarchy).All(button=>!EncounterOfficePanel.Retired(button.command,button.argument)),"the picker is the only console before an interview; findings, assessment, keyboard and retired controls are hidden");
            Check(buttons.Any(button=>button.command=="attending")&&!buttons.Any(button=>(button.command=="attending"||button.command=="voice"||button.command=="stop"||button.command=="refresh")&&button.gameObject.activeInHierarchy),"Present to Jarvis, Voice, Stop and Refresh are gone from the room");
            panel.Act("attending","");Check(session.State==null,"a retired attending command does nothing");
            Check(buttons.Any(button=>button.command=="page"&&button.argument=="patients")&&!buttons.Any(button=>button.command=="patient"&&(button.argument==EncounterContract.FemalePatientId||button.argument==EncounterContract.MalePatientId)),"patient picker uses the service list instead of fixed demo identity buttons");
            Check(File.Exists(EncounterOfficeBuild.SurgeryScenePath),"the existing native surgery scene is present");
            ValidatePointerTabs(rig,panel,buttons);
            // During the interview the dialogue box is the only panel: any other visible console, button, status or label fails.
            foreach(var phase in new[]{"interview","scored","skipped"})
            {
                Property(session,"State",new EncounterState{encounterId=phase=="skipped"?"":"int-scenefixture01",phase=phase,patientId=EncounterContract.FemalePatientId,patientName="Priya Ramaswamy",speaker="patient"});
                panel.Refresh();
                var visible=UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None).Where(renderer=>renderer.enabled&&renderer.gameObject.activeInHierarchy&&renderer.transform.IsChildOf(panel.transform)).ToArray();
                Check(visible.Length==0&&!session.patient.stateLabel.gameObject.activeInHierarchy&&!rig.talkHint.gameObject.activeInHierarchy,"no office panel, button, status line or floating label is visible during the "+phase+" phase: "+string.Join(", ",visible.Select(renderer=>renderer.name).Take(5)));
            }
            Property(session,"State",null);panel.Refresh();
            Check(panel.PickerConsole.gameObject.activeSelf,"leaving the interview brings the picker back");
            // Voice is chosen in the pause menu; the session keeps the explicit on/off contract.
            Check(session.VoiceEnabled,"voice auto-connect is enabled by default");
            Property(session.voice,"Status","connected");session.ToggleVoice();Check(!session.VoiceEnabled&&!session.voice.Connected,"Voice off disconnects and disables auto-connect");
            session.ToggleVoice();Check(session.VoiceEnabled,"Voice on re-enables auto-connect");
            Check(!buttons.Any(button=>button.command=="response_next"||button.command=="response_previous")&&!UnityEngine.Object.FindObjectsByType<TMPro.TextMeshPro>(FindObjectsInactive.Include,FindObjectsSortMode.None).Any(text=>text.name=="Response"),"office panels carry no duplicate transcript region");
            var fittedTexts=UnityEngine.Object.FindObjectsByType<ScalpalTextFit>(FindObjectsInactive.Include,FindObjectsSortMode.None).Where(fit=>fit.GetComponentInParent<EncounterOfficePanel>(true)||fit.GetComponentInParent<EncounterOfficeRig>(true)||fit.GetComponentInParent<EncounterPatientPresentation>(true)).ToArray();
            Check(!UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include,FindObjectsSortMode.None).Any(),"office renders no legacy TextMesh (static SDF TextMeshPro only)");
            var brand=ScalpalBrand.Active;
            Check(fittedTexts.Length>40,"office panels, talk hint and patient label carry brand fitted text");
            foreach(var fit in fittedTexts)
            {
                fit.Fit();var measured=fit.MeasuredSize();
                Check(measured.x<=fit.maximumWidth+.002f&&measured.y<=fit.maximumHeight+.002f,"laid-out SDF text fits its world-space region: "+fit.name+" '"+fit.Text.text+"' measured="+measured.x.ToString("F4")+"x"+measured.y.ToString("F4")+" max="+fit.maximumWidth.ToString("F4")+"x"+fit.maximumHeight.ToString("F4")+" size="+fit.Text.fontSize);
                var material=fit.Text.fontSharedMaterial;
                Check(material==brand.TextMaterial(fit.role)&&fit.Text.font==brand.Font(fit.role)&&material.renderQueue==3020,"brand SDF text renders above glass and buttons: "+fit.name);
            }
            Check(fittedTexts.Where(fit=>fit.role==ScalpalTextRole.Title).All(fit=>fit.Text.font==brand.display)&&fittedTexts.Where(fit=>fit.role!=ScalpalTextRole.Title).All(fit=>fit.Text.font!=brand.display),"panel titles are Instrument Serif; body and buttons are Geist Mono");
            ValidateReadability(panel);
            ValidateDialoguePlacement(session,rig,panel);
            Check(before.SequenceEqual(EditorBuildSettings.scenes.Select(s=>s.path+s.enabled)),"validation preserves surgery build scene settings");
        }
        // The shared dialogue box in the real office layout: lower middle, below the patient's face, clear of the consoles.
        // Authored panel strings in the built layout, measured from the seated clinician's eye.
        static void ValidateReadability(EncounterOfficePanel panel)
        {
            var root=panel.transform;
            var measured=ScalpalBrandLayout.Measure(root,EncounterOfficeBuild.Viewer,true).Where(item=>item.fit.gameObject.activeInHierarchy||item.fit.transform.IsChildOf(panel.keyboard)).ToList();
            UnityEngine.Debug.Log("SCALPAL_ENCOUNTER_TEXT_MM min="+measured.Min(item=>item.mmAt1m).ToString("F1")+" failing="+string.Join(" | ",measured.Where(item=>!item.Passes).Select(item=>item.fit.name+"="+item.mmAt1m.ToString("F1"))));
            foreach(var item in measured)Check(item.Passes,"office text meets its floor (labels 32, body 24 mm/m): "+item.fit.name+" '"+item.fit.Text.text.Split('\n')[0]+"' "+item.mmAt1m.ToString("F1"));
            foreach(Transform console in root)Check(ScalpalPlacement.IsLevel(console),"office console is level with the horizon: "+console.name);
        }
        // The picker's controls are colliders the corrected aim ray can press, from either controller.
        static void ValidatePointerTabs(EncounterOfficeRig rig,EncounterOfficePanel panel,EncounterOfficeButton[] buttons)
        {
            int hand=0;
            foreach(var control in buttons.Where(button=>button.panel==panel&&(button.command=="page"&&button.argument=="patients"||button.command=="reload_patients")).ToArray())
            {
                var result=ScalpalPointerProbe.Press(hand,rig.origin,control.GetComponent<Collider>(),()=>rig.Pointer(hand),rig.StepPointers);
                Check(result.RayMatchesAim&&result.rayVisible&&(result.lineStart-result.expectedOrigin).magnitude<1e-4f,"office ray starts at the "+(hand==0?"left":"right")+" controller aim pose: "+control.command);
                Check(result.hovered&&result.accentOnHover&&panel.Page=="patients","aim ray + trigger presses the picker's "+control.command+" from the "+(hand==0?"left":"right")+" controller");
                hand=1-hand;
            }
        }
        static void ValidateDialoguePlacement(NativeEncounterSession session,EncounterOfficeRig rig,EncounterOfficePanel panel)
        {
            session.patient.Select(AdultFixtures()[0]);
            var feed=Scalpal.Shell.DialogueFeed.Attach();
            try
            {
                Check(feed&&feed.Office==session&&feed.box.viewer==rig.head.transform&&feed.box.avoid.Count==2,"office dialogue feed binds the encounter, tracked head and both lower consoles");
                Check(feed.box.protectedFace&&feed.box.protectedFace.name=="NoseTip"&&feed.box.protectedFace.gameObject.activeInHierarchy,"office dialogue box protects the visible patient's face");
                var eye=rig.head.transform.position;var nose=feed.box.protectedFace.position;
                float Elevation(Vector3 point)=>Mathf.Asin((point.y-eye.y)/(point-eye).magnitude)*Mathf.Rad2Deg;
                float BoxTop()=>Elevation(feed.box.transform.position+feed.box.transform.up*feed.box.CardHeight/2);
                float BoxBottom()=>Elevation(feed.box.transform.position-feed.box.transform.up*feed.box.CardHeight/2);
                feed.box.Say(Scalpal.Shell.DialogueSpeaker.Patient,"Priya Ramaswamy","It started around my belly button.");feed.box.Tick(0);
                float centre=-Elevation(feed.box.transform.position),distance=(feed.box.transform.position-eye).magnitude;
                Check(centre>=19.5f&&centre<=25.5f&&distance>.95f&&distance<1.05f,"interview box sits about 1 m ahead and 20-25 degrees below eye level: "+centre+" deg, "+distance+" m");
                Check(BoxTop()<Elevation(nose)-6,"interview box stays well below the patient's face: top "+BoxTop()+" nose "+Elevation(nose));
                feed.box.ShowChoices("History · Round 1 of 9","What do you do next?",new[]{"A","B","C","D"},new[]{"Ask where the pain started and where it is now","Ask about allergies","Examine the abdomen","Order a CT scan"});
                feed.box.Recenter();feed.box.Tick(0);
                float choicesBottom=feed.box.ChoiceRows.Min(row=>Elevation(row.GetComponent<Renderer>().bounds.min));
                Check(BoxTop()<Elevation(nose)-6&&choicesBottom>-50,"with a round showing the box still clears the face and the last choice stays within a comfortable glance: "+choicesBottom);
                var ray=new Ray(eye,(feed.box.ChoiceRows[1].transform.position-eye).normalized);
                Physics.SyncTransforms();
                Check(Physics.Raycast(ray,out var hit,6)&&hit.collider.GetComponent<Scalpal.Shell.DialogueChoice>()==feed.box.ChoiceRows[1],"the office ray reaches a choice row through the scene (no panel or patient blocks it)");
                UnityEngine.Debug.Log("SCALPAL_DIALOGUE_OFFICE_PLACEMENT interviewCentreDeg=-"+centre.ToString("F1")+" noseDeg="+Elevation(nose).ToString("F1")+" lastChoiceDeg="+choicesBottom.ToString("F1"));
            }
            finally { if(feed)UnityEngine.Object.DestroyImmediate(feed.gameObject);session.patient.Select(null); }
        }
        static bool WeightedBone(SkinnedMeshRenderer[] skins,Transform bone)
        {
            foreach(var skin in skins)
            {
                int index=Array.IndexOf(skin.bones,bone);if(index<0)continue;
                foreach(var weight in skin.sharedMesh.boneWeights)
                    if(weight.boneIndex0==index&&weight.weight0>0||weight.boneIndex1==index&&weight.weight1>0||weight.boneIndex2==index&&weight.weight2>0||weight.boneIndex3==index&&weight.weight3>0)return true;
            }
            return false;
        }
        static EncounterState[] AdultFixtures() => new[]
        {
            new EncounterState { patientId=EncounterContract.FemalePatientId, patientName="Priya Ramaswamy", speaker="patient", patientSex="female", patientAge=40, speakerName="Priya Ramaswamy", speakerSex="female", speakerAge=40, phase="interview" },
            new EncounterState { patientId=EncounterContract.MalePatientId, patientName="Jonah Okoye", speaker="patient", patientSex="male", patientAge=30, speakerName="Jonah Okoye", speakerSex="male", speakerAge=30, phase="interview" }
        };
        static void ValidatePresentationDemographics(EncounterPatientPresentation presentation)
        {
            bool Hidden() => !presentation.female.activeSelf&&!presentation.male.activeSelf;
            presentation.Select(null);
            Check(Hidden()&&presentation.stateLabel.text.Contains("No encounter selected"),"absent authoritative state hides adult templates with an honest waiting label");
            var adult=new EncounterState { patientId="arbitrary-subject-female", patientName="Adult female fixture", speaker="patient", patientSex="female", patientAge=18 };
            presentation.Select(adult);
            Check(presentation.female.activeSelf&&!presentation.male.activeSelf&&presentation.stateLabel.text.Contains("Generic adult female"),"arbitrary subject identity with explicit female adult demographics selects a labeled generic female avatar");
            adult.patientId="arbitrary-subject-male";adult.patientSex="male";adult.patientAge=70;
            presentation.Select(adult);
            Check(presentation.male.activeSelf&&!presentation.female.activeSelf&&presentation.Patient==presentation.male,"arbitrary subject identity with explicit male adult demographics selects the male template");
            float olderScale=presentation.male.transform.localScale.x;adult.patientAge=40;presentation.Select(adult);
            Check(olderScale<presentation.male.transform.localScale.x&&EncounterPatientPresentation.AgeScale(40)==1&&EncounterPatientPresentation.AgeScale(9)<.85f,"an older adult is seated slightly smaller than a working-age adult and a child much smaller");
            float adultScale=presentation.male.transform.localScale.x;
            adult.patientId=EncounterContract.FemalePatientId;
            presentation.Select(adult);
            Check(presentation.male.activeSelf&&!presentation.female.activeSelf,"catalog ID never overrides explicit patient sex");

            string previousVoice=Get<string>(presentation.voice,"encounterVoiceId");
            try
            {
                Set(presentation.voice,"encounterVoiceId","male-voice-fixture");
                var parent=new EncounterState { patientId="child-subject-fixture", patientName="Child fixture", patientSex="male", patientAge=9, speaker="parent", speakerName="Parent fixture", speakerSex="female", speakerAge=35 };
                presentation.Select(parent);
                // Product rule: a pediatric encounter seats the child in the patient chair and the parent beside them.
                Check(presentation.Patient==presentation.male&&presentation.male.activeSelf&&presentation.Companion==presentation.female&&presentation.female.activeSelf&&presentation.Speaker==presentation.female&&Get<string>(presentation.voice,"encounterVoiceId")=="male-voice-fixture","pediatric encounter seats a scaled child and the speaking parent beside them, independent of configured voice identity");
                Check(presentation.male.transform.localScale.x<.85f*adultScale&&presentation.male.transform.localPosition.y>.05f&&Vector3.Distance(presentation.female.transform.position,presentation.male.transform.position)>.8f,"child figure is scaled down, lifted onto the seat, and the parent sits in the companion chair");
                Check(presentation.stateLabel.text.Contains("Parent fixture · parent speaking for Child fixture")&&presentation.stateLabel.text.Contains("child"),"parent label identifies named speaker and named child patient");
                parent.speakerSex="male";parent.patientSex="female";
                presentation.Select(parent);
                Check(presentation.Patient==presentation.female&&presentation.Companion==presentation.male&&presentation.Speaker==presentation.male,"male parent for female child seats both, parent speaking");
                var jaw=presentation.male.GetComponentsInChildren<Transform>(true).Single(node=>node.name=="JawPivot");var jawRest=jaw.localRotation;
                presentation.SetState("speaking");Call(presentation,"ApplyAnimation",.2f,0f,1f,true);
                parent.speakerSex="male";parent.patientSex="male";
                presentation.Select(parent);
                Check(Quaternion.Angle(jaw.localRotation,jawRest)<.001f,"reselection restores the previous speaker's speaking pose");
                Check(presentation.Patient==presentation.male&&presentation.Companion&&presentation.Companion!=presentation.male&&presentation.Companion.activeSelf&&presentation.Speaker==presentation.Companion,"same-sex parent and child get two seated figures, the parent a separate instance");
                parent.speakerAge=0;parent.speakerSex=null;
                presentation.Select(parent);
                Check(presentation.Patient&&presentation.Patient.activeSelf&&presentation.Companion&&presentation.Companion.activeSelf,"missing parent demographics still seat a generic adult parent rather than an empty chair");
                parent.speaker="patient";
                presentation.Select(parent);
                Check(presentation.Patient&&presentation.Patient.activeSelf&&!presentation.Companion&&presentation.Speaker==presentation.Patient&&presentation.Patient.transform.localScale.x<.85f*adultScale,"a child speaking for themself is seated as a scaled child with no parent");
            }
            finally { Set(presentation.voice,"encounterVoiceId",previousVoice);presentation.Select(null); }
            foreach(var sex in new string[] { null,"unknown","nonbinary" })
            {
                adult.patientSex=sex;presentation.Select(adult);
                Check(presentation.Patient&&presentation.Patient.activeSelf&&presentation.stateLabel.text.Contains("Generic adult"),"unknown or unsupported patient sex still seats a generic adult: "+sex);
            }
            adult.patientSex="female";adult.patientAge=0;presentation.Select(adult);
            Check(presentation.female.activeSelf&&presentation.female.transform.localScale.x>.99f*adultScale,"missing patient age seats an adult-size avatar");
            adult.patientAge=40;adult.speaker="unknown";presentation.Select(adult);
            Check(presentation.female.activeSelf&&presentation.Speaker==presentation.female,"unknown speaker role seats the patient");
            presentation.Select(null);
            Check(!presentation.female.activeSelf&&!presentation.male.activeSelf&&!presentation.Patient,"no encounter leaves the chair empty");
        }
        static void ValidatePatientAnimation(NativeEncounterSession session)
        {
            var presentation=session.patient;
            Check(presentation.voice==session.voice,"patient animation samples the same authoritative native playback transport");
            var animation=typeof(EncounterPatientPresentation).GetMethod("ApplyAnimation",Private);
            void Step(float level,bool playbackActive,float time=0) => animation.Invoke(presentation,new object[]{.2f,time,level,playbackActive});
            foreach(var state in AdultFixtures())
            {
                var id=state.patientId;
                presentation.Select(state);
                var model=id==EncounterContract.FemalePatientId?presentation.female:presentation.male;
                var nodes=model.GetComponentsInChildren<Transform>(true);
                var head=nodes.Single(node=>node.name=="HeadPivot");var jaw=nodes.Single(node=>node.name=="JawPivot");var chest=nodes.Single(node=>node.name=="spine02");
                var headRest=head.localRotation;var jawRest=jaw.localRotation;var chestRest=chest.localRotation;
                var skins=model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Check(WeightedBone(skins,chest),"subtle breathing uses the real weighted donor torso bone: "+id);
                var skin=skins.First(renderer=>renderer.sharedMaterials.Any(material=>material.name.Contains("Skin")));
                presentation.SetState("speaking");Step(0,true);var baseline=BakedVertices(skin);
                Step(1,true);var voiced=BakedVertices(skin);
                // Arms gesture independently while voiced (ValidateArmAnimation); jaw and breathing are measured off the arm chain.
                var body=BodyVertices(skin);
                float jawMotion=MaximumMotion(baseline,voiced,body);
                UnityEngine.Debug.Log("SCALPAL_PATIENT_JAW_DEFORMATION patient="+id+" jawDegrees="+Quaternion.Angle(jaw.localRotation,jawRest)+" maxVertexMetres="+jawMotion+" rendererScale="+skin.transform.lossyScale);
                Check(Quaternion.Angle(jaw.localRotation,jawRest)>8&&jawMotion>.002f&&jawMotion<.04f,"controlled nonzero playback envelope rotates and deforms the imported human jaw by 2–40mm: "+id+" maxVertexMetres="+jawMotion);
                Step(0,true);Check(Quaternion.Angle(jaw.localRotation,jawRest)<.001f,"actual playback silence closes the jaw even while agent mode says speaking: "+id);
                Step(.2f,true);float soft=Quaternion.Angle(jaw.localRotation,jawRest);Step(0,true);Step(1,true);
                Check(soft>0&&Quaternion.Angle(jaw.localRotation,jawRest)>soft,"jaw opening follows audio amplitude rather than a clock-driven talking cycle: "+id);
                presentation.SetState("listening");Step(1,true);Check(Quaternion.Angle(jaw.localRotation,jawRest)>8,"last audible syllable animates even if provider mode already returned to listening: "+id);
                presentation.SetState("speaking");
                Step(1,false);Check(Quaternion.Angle(jaw.localRotation,jawRest)<.001f,"disconnected transport closes patient mouth despite stale speaking mode: "+id);
                presentation.SetState("resting");Step(1,true);Check(Quaternion.Angle(jaw.localRotation,jawRest)<.001f,"attending/resting patient does not mouth the attending's playback: "+id);
                // Sample near a breath peak; t=2.1 gave only .031 degrees, below Quaternion.Angle's near-equal resolution.
                presentation.SetState("listening");Step(0,false,1f);
                float headDegrees=Quaternion.Angle(head.localRotation,headRest),chestDegrees=Quaternion.Angle(chest.localRotation,chestRest),idleMotion=MaximumMotion(baseline,BakedVertices(skin),body);
                UnityEngine.Debug.Log("SCALPAL_PATIENT_IDLE_DEFORMATION patient="+id+" headDegrees="+headDegrees+" torsoDegrees="+chestDegrees+" maxVertexMetres="+idleMotion);
                Check(headDegrees>.05f&&chestDegrees>.01f&&idleMotion>.0001f&&idleMotion<.03f,"idle/listening head and breathing animate real weighted skin within 0.1–30mm without voice: "+id+" headDegrees="+headDegrees+" torsoDegrees="+chestDegrees+" maxVertexMetres="+idleMotion);
                presentation.SetState("speaking");Step(1,true);
                Call(presentation,"OnApplicationFocus",false);Step(1,true,2.1f);
                Check(AtRest(head,headRest,jaw,jawRest,chest,chestRest),"focus loss restores all bones and prevents stale playback from moving the suspended patient: "+id);
                Call(presentation,"OnApplicationFocus",true);Step(1,true);
                Call(presentation,"OnApplicationPause",true);Check(AtRest(head,headRest,jaw,jawRest,chest,chestRest),"application pause restores donor pose: "+id);
                Call(presentation,"OnApplicationPause",false);Step(1,true);
                Call(presentation,"OnDisable");Check(AtRest(head,headRest,jaw,jawRest,chest,chestRest),"disable restores the authored head, jaw and torso pose: "+id);
                Step(1,true);presentation.Select(AdultFixtures().Single(other=>other.patientSex!=state.patientSex));
                Check(AtRest(head,headRest,jaw,jawRest,chest,chestRest)&&!model.activeSelf,"case switch restores old human before hiding it and clears the audio envelope: "+id);
            }
            presentation.Select(AdultFixtures()[0]);
        }
        // Arms are the people's body language: the appendicitis patient guards the right lower belly, whoever is voicing
        // gestures and then settles back, and no pose may reach through the torso, the lap or a chair. Each figure must
        // animate its own sex's rig, so a male encounter can never be driven (or seen) through the female skeleton.
        static void ValidateArmAnimation(EncounterPatientPresentation presentation)
        {
            var animation=typeof(EncounterPatientPresentation).GetMethod("ApplyAnimation",Private);
            float clock=0;
            void Run(float seconds,bool voiced,string state)
            {
                presentation.SetState(state);
                for(float elapsed=0;elapsed<seconds;elapsed+=.05f){clock+=.05f;animation.Invoke(presentation,new object[]{.05f,clock,voiced?.45f+.35f*Mathf.Sin(clock*11):0f,voiced});}
            }
            Transform[] Arms(GameObject model){var nodes=model.GetComponentsInChildren<Transform>(true);return EncounterPatientPresentation.ArmBones.Select(name=>nodes.Single(node=>node.name==name)).ToArray();}
            Quaternion[] Pose(Transform[] bones)=>bones.Select(bone=>bone.localRotation).ToArray();
            float Moved(Transform[] bones,Quaternion[] from,int first=0,int count=6){float most=0;for(int i=first;i<first+count;i++)most=Mathf.Max(most,Quaternion.Angle(bones[i].localRotation,from[i]));return most;}
            // Seated body frame from the donor's own face direction: forward toward the clinician, x to the person's right.
            Vector3 Body(GameObject model,Vector3 point)
            {
                var nodes=model.GetComponentsInChildren<Transform>(true);var spine=nodes.Single(node=>node.name=="spine03");
                var forward=Vector3.ProjectOnPlane(nodes.Single(node=>node.name=="NoseTip").position-nodes.Single(node=>node.name=="HeadPivot").position,Vector3.up).normalized;
                var offset=(point-spine.position)/model.transform.lossyScale.x;
                return new Vector3(Vector3.Dot(offset,Vector3.Cross(Vector3.up,forward)),offset.y,Vector3.Dot(offset,forward));
            }
            // A hand's fingertip region, 9cm along the hand bone of an adult-size figure.
            Vector3 Tip(GameObject model,Transform wrist)=>wrist.position+wrist.up*.09f*model.transform.lossyScale.x;
            // Office geometry as temporary colliders: forearm and hand samples must stay clear of chair, armrests and desk.
            var office=GameObject.Find("BotanicalDoctorOffice");var colliders=new List<MeshCollider>();
            foreach(var filter in office.GetComponentsInChildren<MeshFilter>(true)){var collider=filter.gameObject.AddComponent<MeshCollider>();collider.sharedMesh=filter.sharedMesh;colliders.Add(collider);}
            Physics.SyncTransforms();
            string Clash(GameObject model,Transform[] bones)
            {
                float scale=model.transform.lossyScale.x;
                for(int side=0;side<2;side++)
                {
                    var forearm=bones[side*3+1];var wrist=bones[side*3+2];var tip=Tip(model,wrist);
                    for(int k=0;k<=6;k++)
                    {
                        var point=k<=4?Vector3.Lerp(forearm.position,wrist.position,k/4f):Vector3.Lerp(wrist.position,tip,(k-4)/2f);
                        var hit=Physics.OverlapSphere(point,.03f*scale).FirstOrDefault(collider=>colliders.Contains(collider));
                        if(hit)return model.name+" "+bones[side*3].name+" sample "+k+" touches "+hit.name;
                    }
                    // In front of the abdomen (not through it) and above the seat cushion.
                    var local=Body(model,tip);
                    if(local.z<.06f)return model.name+" "+wrist.name+" hand is inside the torso: forward="+local.z;
                    if(tip.y<EncounterPatientPresentation.SeatHeight+.05f*scale)return model.name+" "+wrist.name+" hand sinks into the seat: y="+tip.y;
                }
                return "";
            }
            string Limits(Transform[] bones,Quaternion[] imported){for(int i=0;i<6;i++){float degrees=Quaternion.Angle(bones[i].localRotation,imported[i]);if(degrees>EncounterPatientPresentation.ArmLimit[i%3]+.25f)return bones[i].name+"="+degrees;}return "";}
            try
            {
                foreach(var state in AdultFixtures())
                {
                    bool female=state.patientSex=="female";var model=female?presentation.female:presentation.male;var other=female?presentation.male:presentation.female;
                    presentation.Select(null);
                    var arms=Arms(model);var otherArms=Arms(other);var imported=Pose(arms);var otherImported=Pose(otherArms);var importedHand=Body(model,Vector3.Lerp(arms[2].position,Tip(model,arms[2]),.5f));
                    var skins=model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    presentation.Select(state);
                    var jaw=Get<Transform>(presentation,"jaw");var head=Get<Transform>(presentation,"head");
                    Check(presentation.Patient==model&&model.activeSelf&&!other.activeSelf&&jaw&&jaw.IsChildOf(model.transform)&&head&&head.IsChildOf(model.transform),"a "+state.patientSex+" encounter selects the "+state.patientSex+" model and animates its own head/jaw bones");
                    Check(arms.All(bone=>WeightedBone(skins,bone)),"arm animation drives weighted upper-arm, forearm and hand bones of the "+state.patientSex+" donor rig");
                    Run(3,false,"listening");var rest=Pose(arms);
                    Check(Moved(arms,imported)>3&&Moved(otherArms,otherImported)<.001f,"the "+state.patientSex+" model's arms take the resting pose; the hidden "+(female?"male":"female")+" rig is untouched");
                    var guard=Body(model,Vector3.Lerp(arms[2].position,Tip(model,arms[2]),.5f)); // palm centre
                    Check(guard.x>.03f&&guard.y<importedHand.y-.02f&&guard.z<importedHand.z-.03f,"resting appendicitis patient lowers the right hand onto the right lower abdomen: "+state.patientSex+" hand="+guard.ToString("F3")+" imported="+importedHand.ToString("F3"));
                    Check(Clash(model,arms)=="","resting arms clear torso, lap and chair: "+Clash(model,arms));
                    float most=0,spread=0;var first=Pose(arms);string clash="",limits="";
                    for(int i=0;i<80;i++)
                    {
                        Run(.1f,true,"speaking");
                        most=Mathf.Max(most,Moved(arms,rest,3,3));spread=Mathf.Max(spread,Moved(arms,first,3,3));
                        if(clash=="")clash=Clash(model,arms);if(limits=="")limits=Limits(arms,imported);
                    }
                    Check(most>8&&spread>4,"while voicing, the free arm gestures away from rest and keeps varying: "+state.patientSex+" most="+most+" spread="+spread);
                    Check(clash=="","gesturing arms never clip torso, lap or chair: "+clash);
                    Check(limits=="","arm offsets stay within the conservative per-bone limits: "+limits);
                    // Drive to a wince: the guarding hand presses closer into the belly and the head dips.
                    float wince=Enumerable.Range(0,4000).Select(i=>i*.05f).First(time=>time>clock+2&&EncounterPatientPresentation.Wince(time)>.97f);
                    Run(wince-clock-3,true,"speaking");var beforeWince=Body(model,Tip(model,arms[2])).z;Run(3,true,"speaking");
                    Check(Body(model,Tip(model,arms[2])).z<beforeWince-.005f&&Clash(model,arms)=="","an occasional wince presses the guarding hand toward the belly without clipping: "+state.patientSex);
                    Run(6,false,"listening");
                    Check(Moved(arms,rest)<1.5f,"after speaking the arms settle back to the resting pose: "+state.patientSex+" residual="+Moved(arms,rest));
                    Call(presentation,"OnDisable");
                    Check(Moved(arms,imported)<.001f,"disable restores the imported arm pose: "+state.patientSex);
                }
                // Pediatric: the speaking parent beside the child gestures; the child only guards and breathes.
                var parent=new EncounterState{patientId="child-subject-fixture",patientName="Child fixture",patientSex="male",patientAge=9,speaker="parent",speakerName="Parent fixture",speakerSex="female",speakerAge=35};
                foreach(var sex in new[]{"female","male"})
                {
                    parent.speakerSex=sex;presentation.Select(null);presentation.Select(parent);
                    var parentArms=Arms(presentation.Companion);var childArms=Arms(presentation.Patient);
                    Check(presentation.Companion.name.StartsWith(sex=="female"?"GenericAdultFemale":"GenericAdultMale",StringComparison.Ordinal)&&!presentation.Patient.name.Contains("Female"),"a "+sex+" parent is seated with the "+sex+" rig beside the male child");
                    Run(3,false,"listening");var parentRest=Pose(parentArms);var childRest=Pose(childArms);
                    Check(Clash(presentation.Companion,parentArms)==""&&Clash(presentation.Patient,childArms)=="","seated parent and child arms clear their chairs at rest: "+Clash(presentation.Companion,parentArms)+Clash(presentation.Patient,childArms));
                    float parentMost=0,childMost=0;string clash="";
                    for(int i=0;i<80;i++)
                    {
                        Run(.1f,true,"speaking");parentMost=Mathf.Max(parentMost,Moved(parentArms,parentRest));childMost=Mathf.Max(childMost,Moved(childArms,childRest));
                        if(clash=="")clash=Clash(presentation.Companion,parentArms)+Clash(presentation.Patient,childArms);
                    }
                    Check(parentMost>8&&childMost<2,"the speaking "+sex+" parent companion gestures while the listening child stays still: parent="+parentMost+" child="+childMost);
                    Check(clash=="","parent and child arms never clip their chairs while the parent talks: "+clash);
                    Run(6,false,"listening");
                    Check(Moved(parentArms,parentRest)<1.5f,"the parent's arms settle back after speaking");
                }
            }
            finally { foreach(var collider in colliders)UnityEngine.Object.DestroyImmediate(collider);presentation.Select(null); }
        }
        static bool AtRest(Transform head,Quaternion headRest,Transform jaw,Quaternion jawRest,Transform chest,Quaternion chestRest) =>
            Quaternion.Angle(head.localRotation,headRest)<.001f&&Quaternion.Angle(jaw.localRotation,jawRest)<.001f&&Quaternion.Angle(chest.localRotation,chestRest)<.001f;
        static Vector3[] BakedVertices(SkinnedMeshRenderer skin)
        {
            // Compensate renderer scale in the bake before TransformPoint applies that scale once in world meters.
            var mesh=new Mesh();try { skin.BakeMesh(mesh,true);return mesh.vertices.Select(vertex=>skin.transform.TransformPoint(vertex)).ToArray(); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static float MaximumMotion(Vector3[] before,Vector3[] after,bool[] include=null)
        {
            float maximum=0;for(int i=0;i<before.Length;i++)if(include==null||include[i])maximum=Mathf.Max(maximum,Vector3.Distance(before[i],after[i]));return maximum;
        }
        // Skin vertices with no weight on either arm chain (upper arm down to the fingers).
        static bool[] BodyVertices(SkinnedMeshRenderer skin)
        {
            var arm=skin.bones.Select(bone=>bone&&skin.bones.Any(other=>other&&other.name.StartsWith("upperarm01.",StringComparison.Ordinal)&&bone.IsChildOf(other))).ToArray();
            return skin.sharedMesh.boneWeights.Select(weight=>!(weight.weight0>0&&arm[weight.boneIndex0]||weight.weight1>0&&arm[weight.boneIndex1]||weight.weight2>0&&arm[weight.boneIndex2]||weight.weight3>0&&arm[weight.boneIndex3])).ToArray();
        }
        static void ValidateSpeechAndTalk(EncounterPatientPresentation presentation)
        {
            var manifestAsset=Resources.Load<TextAsset>("EncounterSpeech/manifest");
            Check(manifestAsset,"authored offline speech manifest is bundled");
            var manifest=JsonUtility.FromJson<SpeechManifest>(manifestAsset.text);
            Check(manifest.speakerRole=="patient"&&manifest.entries!=null&&manifest.entries.Length>2,"offline manifest contains patient recordings and authored facts");
            foreach(var entry in manifest.entries)
            {
                var clip=EncounterPatientSpeech.Find(entry.patientId,entry.tool,entry.argument,entry.exactDisplayText);
                Check(clip&&clip==Resources.Load<AudioClip>(entry.resourcePath),"exact authored fact resolves its committed speech clip: "+entry.resourcePath);
                Check(!EncounterPatientSpeech.Find(entry.patientId,entry.tool,entry.argument,entry.exactDisplayText+" changed")&&!EncounterPatientSpeech.Find("wrong-patient",entry.tool,entry.argument,entry.exactDisplayText),"changed patient/text cannot select another authored recording: "+entry.resourcePath);
            }
            GameObject fixture=null;QuestJarvisVoice voice=null;var previousVoice=presentation.voice;
            try
            {
                fixture=new GameObject("EncounterHoldAndAudioFixture");fixture.SetActive(false);
                voice=fixture.AddComponent<QuestJarvisVoice>();var session=fixture.AddComponent<NativeEncounterSession>();session.voice=voice;
                var holdState=AdultFixtures()[0];holdState.encounterId="int-holdfixture01";Property(session,"State",holdState);
                Set(session,"encounterId",holdState.encounterId);Set(session,"patientId",holdState.patientId);
                Call(session,"OnEnable");Property(voice,"Status","connected");
                Check(!session.TalkHeld&&voice.MicrophoneMuted,"the interview begins with the microphone muted");
                session.SetTalkHeld(true);Check(!session.TalkHeld&&voice.MicrophoneMuted&&!voice.AnswerRecording,"holding the answer button before a round is showing records nothing");
                Set(session,"round",FixtureRound(1));
                // A live connection's microphone is tapped for the answer; a synthetic clip stands in for the device.
                var tap=AudioClip.Create("AnswerTapFixture",16000,1,16000,false);Set(voice,"microphoneClip",tap);Set(voice,"microphoneRate",16000);
                session.SetTalkHeld(true);
                Check(session.TalkHeld&&voice.AnswerRecording&&voice.MicrophoneMuted,"holding records the answer locally while the agent microphone stays muted");
                Call(voice,"AppendAnswer",Tone(16000,1.2f,.4f),1);
                session.SetTalkHeld(false);
                Check(!session.TalkHeld&&!voice.AnswerRecording&&voice.MicrophoneMuted,"release stops recording and the microphone is still muted");
                var answerOperation=Get<IEnumerable>(session,"pending").Cast<object>().Last();
                string answerBody=FieldOf<string>(answerOperation,"body");
                var sent=JsonUtility.FromJson<InterviewAnswerAudio>(answerBody);var wav=Convert.FromBase64String(sent.audio);
                Check(FieldOf<string>(answerOperation,"path")=="/interviews/int-holdfixture01/answer"&&sent.mimeType=="audio/wav"&&Encoding.ASCII.GetString(wav,0,4)=="RIFF"&&Encoding.ASCII.GetString(wav,8,4)=="WAVE"&&BitConverter.ToInt32(wav,24)==16000&&wav.Length==44+16000*1.2f*2,"a held spoken answer posts a mono 16-bit WAV {audio, mimeType} to the interview");
                Set(voice,"microphoneClip",null);Call(session,"Invalidate");Set(session,"round",FixtureRound(1));Set(voice,"microphoneClip",tap);
                session.SetTalkHeld(true);Call(voice,"AppendAnswer",Tone(16000,.1f,.4f),1);session.SetTalkHeld(false);
                Check(session.AnswerNote.StartsWith("Too short",StringComparison.Ordinal)&&!session.Busy,"a tap of the answer button is too short to send and says so");
                session.SetTalkHeld(true);Call(voice,"AppendAnswer",Tone(16000,25f,.4f),1);
                Check(Get<List<float>>(voice,"answerSamples").Count==(int)(16000*QuestJarvisVoice.MaxAnswerSeconds),"a spoken answer is capped under the service's 20 second limit");
                Call(session,"OnApplicationFocus",false);
                Check(!session.TalkHeld&&!voice.AnswerRecording&&voice.MicrophoneMuted,"focus loss cancels a held answer");
                Set(voice,"microphoneClip",null);
                Property(voice,"Status","disconnected");
                Call(session,"OnApplicationFocus",true);
                // Quest drops focus constantly; a live interview must reconnect its (muted-mic) patient voice on return.
                if(session.VoiceEnabled&&session.State!=null&&session.State.phase=="interview")
                    Check(voice.Status!="disconnected"&&voice.Status!="offline","focus return attempts to reconnect the interview patient voice (fixture has no provider): "+voice.Status);
                UnityEngine.Object.DestroyImmediate(tap);
                Property(voice,"Status","connected");Call(voice,"OnApplicationFocus",false);
                Check(!voice.Connected&&voice.MicrophoneMuted&&!voice.PlaybackActive,"focus loss disconnects active speech");
                Call(voice,"OnApplicationFocus",true);
                Property(voice,"Status","connected");Call(session,"Invalidate");
                Check(!session.TalkHeld&&voice.MicrophoneMuted&&!voice.PlaybackActive&&session.Round==null,"case/reset invalidation clears held input, round, playback and keeps the microphone muted");
                Call(session,"OnDisable");
                presentation.voice=voice;presentation.Select(AdultFixtures()[0]);presentation.SetState("speaking");
                var jaw=presentation.female.GetComponentsInChildren<Transform>(true).Single(node=>node.name=="JawPivot");var rest=jaw.localRotation;
                Property(voice,"Status","connected");var samples=Get<Queue<float>>(voice,"outputSamples");
                for(int i=0;i<512;i++)samples.Enqueue(.25f);var buffer=new float[512];Call(voice,"ReadAudio",buffer);
                Call(presentation,"ApplyAnimation",.2f,0f,voice.PlaybackLevel,voice.PlaybackActive);
                Check(buffer.All(sample=>sample==.25f)&&voice.PlaybackLevel>.9f&&Quaternion.Angle(jaw.localRotation,rest)>8,"actual queued PCM callback drives the bound patient's jaw through measured playback level");
                Call(voice,"ReadAudio",buffer);Call(presentation,"LateUpdate");
                Check(buffer.All(sample=>sample==0)&&voice.PlaybackLevel==0&&Quaternion.Angle(jaw.localRotation,rest)<.001f,"empty actual PCM queue produces silence and closes jaw on production update");
                for(int i=0;i<512;i++)samples.Enqueue(.25f);Call(voice,"ReadAudio",buffer);Call(voice,"ResetPlaybackLevel");
                Check(voice.PlaybackLevel==0,"playback reset clears the measured audio envelope");
                for(int i=0;i<512;i++)samples.Enqueue(.25f);Call(voice,"ReadAudio",buffer);
                Set(voice,"playbackTimestamp",Stopwatch.GetTimestamp()-Stopwatch.Frequency);Check(voice.PlaybackLevel==0,"stale consumed PCM cannot hold the mouth open");
                voice.Disconnect();Call(presentation,"LateUpdate");
                Check(!voice.PlaybackActive&&voice.PlaybackLevel==0&&Quaternion.Angle(jaw.localRotation,rest)<.001f,"disconnect resets actual playback activity/envelope and mouth");
                // Condition this visual-answer fixture as explicitly resumed; no provider connection is opened.
                Property(session,"Suspended",false);
                Set(voice,"localSpeech",true);Set(session,"patientId",EncounterContract.FemalePatientId);
                for(int i=0;i<512;i++)samples.Enqueue(.25f);Call(voice,"ReadAudio",buffer);
                Call(session,"PlayAuthoredSpeech","A changed line with no authored recording.");
                Check(!voice.PlaybackActive&&voice.PlaybackLevel==0&&samples.Count==0,"a visual-only/mismatched answer interrupts the previous offline utterance rather than speaking stale facts");
                foreach(var state in AdultFixtures())
                {
                    var id=state.patientId;
                    var entry=manifest.entries.Single(item=>item.patientId==id&&item.tool=="greeting");
                    var clip=EncounterPatientSpeech.Find(id,entry.tool,entry.argument,entry.exactDisplayText);
                    var source=new float[clip.samples*clip.channels];Check(clip.GetData(source,0)&&source.Any(sample=>Mathf.Abs(sample)>.02f),"bundled greeting exposes real readable speech PCM: "+id);
                    Check(voice.PlayLocalSpeech(clip)&&voice.PlaybackActive&&!voice.Connected&&Get<AudioClip>(voice,"microphoneClip")==null&&Get<object>(voice,"active")==null,"bundled speech queues through native playback without microphone/provider connection: "+id);
                    presentation.Select(state);presentation.SetState("speaking");var active=id==EncounterContract.FemalePatientId?presentation.female:presentation.male;
                    jaw=active.GetComponentsInChildren<Transform>(true).Single(node=>node.name=="JawPivot");rest=jaw.localRotation;
                    bool moved=false;buffer=new float[1024];int remaining=Get<Queue<float>>(voice,"outputSamples").Count;
                    for(int offset=0;offset<remaining;offset+=buffer.Length)
                    {
                        Call(voice,"ReadAudio",buffer);float level=voice.PlaybackLevel;
                        Call(presentation,"ApplyAnimation",.1f,0f,level,voice.PlaybackActive);
                        if(level>.1f&&Quaternion.Angle(jaw.localRotation,rest)>1) { moved=true;break; }
                    }
                    Check(moved,"actual bundled greeting PCM amplitude deforms patient jaw: "+id);
                    DisposeStreamingClip(voice);voice.Disconnect();Call(presentation,"LateUpdate");
                    Check(voice.PlaybackLevel==0&&!voice.PlaybackActive&&Quaternion.Angle(jaw.localRotation,rest)<.001f,"offline speech cancellation closes mouth and clears queued playback: "+id);
                }
            }
            finally
            {
                if(voice) { DisposeStreamingClip(voice);voice.Disconnect(); }
                presentation.voice=previousVoice;presentation.Select(AdultFixtures()[0]);
                if(fixture)UnityEngine.Object.DestroyImmediate(fixture);
            }
        }
        static void DisposeStreamingClip(QuestJarvisVoice voice)
        {
            var speaker=Get<AudioSource>(voice,"speaker");if(speaker) { speaker.Stop();speaker.clip=null; }
            var clip=Get<AudioClip>(voice,"playbackClip");Set(voice,"playbackClip",null);if(clip)UnityEngine.Object.DestroyImmediate(clip);
        }
        static void ValidateContracts()
        {
            Check(!EncounterContract.HasError(JsonUtility.FromJson<EncounterReply>("{\"result\":\"Recorded.\"}")) && EncounterContract.HasError(JsonUtility.FromJson<EncounterReply>("{\"error\":{\"code\":\"no_interview\",\"message\":\"No interview\"}}")),"JsonUtility absent/empty error object is not a service error; nonempty error content is");
            Check(EncounterContract.ValidOfficeId("int-0123456789abcdef")&&EncounterContract.ValidOfficeId("enc-0123456789")&&!EncounterContract.ValidOfficeId("int-UPPER123")&&!EncounterContract.ValidOfficeId("coach-123456"),"office ids are interview (int-) or legacy encounter (enc-) ids");
            Check(EncounterContract.OfficePath("int-0123456789abcdef")=="/interviews/int-0123456789abcdef"&&EncounterContract.OfficePath("enc-0123456789")=="/encounters/enc-0123456789","the OR rechecks an interview at /interviews and a legacy encounter at /encounters");
            var live=EncounterContract.ReadOffice("{\"interviewId\":\"int-0123456789abcdef\",\"phase\":\"scored\",\"patientId\":\"patient-demo-sparse\",\"patientName\":\"Jonah Okoye\",\"round\":null,\"picks\":[],\"findings\":[]}","int-0123456789abcdef");
            Check(live.state!=null&&live.state.encounterId=="int-0123456789abcdef"&&live.state.phase=="scored"&&live.state.patientId=="patient-demo-sparse"&&live.patient==null,"GET /interviews/:id reads into the handoff's encounter state");
            var scored=EncounterContract.ReadOffice("{\"scorecard\":{\"kind\":\"interview\",\"total\":64,\"max\":100}}","int-0123456789abcdef");
            Check(scored.state==null&&scored.scorecard.kind=="interview"&&scored.scorecard.total==64,"GET /interviews/:id/score carries only the scorecard");
            var card=InterviewScore(5);card.feedback[0]="Missed: Where to start? The best move was...";card.feedback[1]="Close: almost";
            string text=EncounterContract.CompactScore(card);var lines=text.Split('\n');
            Check(lines.Count(line=>line.StartsWith("Feedback item",StringComparison.Ordinal))==3&&!text.Contains("Missed: Where")&&lines.Count(line=>line.StartsWith("Missed · ",StringComparison.Ordinal))==2
                &&text.Contains("Missed · Next? Right answer: Examine the abdomen")&&text.Contains("Missed · Plan? Right answer: Open appendectomy")&&!text.Contains("Ask where it hurts"),"compact scorecard: three key feedback lines and each missed round with the right answer, nothing else");
            Check(EncounterContract.Picked(card,"plan")=="Observe overnight"&&EncounterContract.Best(card,"diagnosis")=="Acute appendicitis","plan and diagnosis picks feed the theatre challenge");
            var wav=QuestJarvisVoice.EncodeWav(new[]{0f,.5f,-.5f,1f},16000);
            Check(wav.Length==52&&Encoding.ASCII.GetString(wav,0,4)=="RIFF"&&BitConverter.ToInt32(wav,4)==44&&Encoding.ASCII.GetString(wav,8,8)=="WAVEfmt "&&BitConverter.ToInt16(wav,20)==1&&BitConverter.ToInt16(wav,22)==1&&BitConverter.ToInt32(wav,24)==16000&&BitConverter.ToInt16(wav,34)==16&&Encoding.ASCII.GetString(wav,36,4)=="data"&&BitConverter.ToInt32(wav,40)==8&&BitConverter.ToInt16(wav,46)==16384,"spoken answers encode as mono PCM16 RIFF/WAVE");
            var state=new EncounterState{encounterId="int-test000000",patientId=EncounterContract.FemalePatientId,patientName="Priya Ramaswamy",speaker="patient",speakerName="Priya Ramaswamy",phase="interview"};
            var theo=new EncounterState{patientName="Theo Abernathy",speaker="parent",speakerName="Laura Abernathy"};
            string[] labels={EncounterContract.SpeakerLabel(state,"patient"),EncounterContract.SpeakerLabel(theo,"patient"),EncounterContract.LearnerLabel};
            Check(labels.SequenceEqual(new[]{"Priya Ramaswamy · Patient","Laura Abernathy · Parent of Theo","You"}),"patient, parent and learner lines carry distinct speaker labels");
            Check(labels.All(label=>!EncounterOfficePanel.ResponseHeader(label,0).Contains("Jarvis")),"no office line is labelled Jarvis");
        }
        // Authored answers for fixture runs: the committed interview content, never sent by the client.
        [Serializable] sealed class AuthoredChoice { public string key, grade; public AuthoredFinding finding; }
        [Serializable] sealed class AuthoredFinding { public string label, text; }
        [Serializable] sealed class AuthoredRound { public string id, stage; public AuthoredChoice[] choices; }
        [Serializable] sealed class AuthoredInterview { public AuthoredRound[] rounds; public string closingLine; }
        static AuthoredInterview Authored(string patient)
        {
            string path=Path.GetFullPath(Path.Combine(Application.dataPath,"../../../services/preop/content/patients/"+patient+"/interview.json"));
            return File.Exists(path)?JsonUtility.FromJson<AuthoredInterview>(File.ReadAllText(path)):null;
        }
        static void ValidateExchange()
        {
            Process server=null;GameObject fixture=null,liveVoiceObject=null;QuestJarvisVoice voice=null;var connections=new List<object>();
            try
            {
                string repo=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.."));string service=Path.Combine(repo,"services/preop");
                var info=new ProcessStartInfo("/usr/bin/env","node --import "+Quote(Path.Combine(service,"node_modules/tsx/dist/loader.mjs"))+" "+Quote(Path.Combine(repo,"scripts/quest/native-coach-check/server.ts"))) { WorkingDirectory=service,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
                server=Process.Start(info);var ready=server.StandardOutput.ReadLineAsync();if(!ready.Wait(15000))throw new InvalidOperationException("Interview fixture did not become ready");
                const string marker="SCALPAL_COACH_TEST_ENDPOINT=";string line=ready.Result??"";if(!line.StartsWith(marker,StringComparison.Ordinal))throw new InvalidOperationException("Interview fixture failed startup");
                string endpoint=line.Substring(marker.Length);Check(new Uri(endpoint).Host=="127.0.0.1","actual service fixture is isolated loopback with external fetch disabled");
                fixture=new GameObject("EncounterOfficeFixture");fixture.SetActive(false);
                var session=fixture.AddComponent<NativeEncounterSession>();session.baseUrl=endpoint;
                session.patient=UnityEngine.Object.FindFirstObjectByType<EncounterPatientPresentation>();
                var dialogue=Scalpal.Shell.DialogueBox.Create(Scalpal.Shell.DialogueBoxStyle.Load(),null);dialogue.transform.SetParent(fixture.transform,false);
                var dialogueFeed=dialogue.gameObject.AddComponent<Scalpal.Shell.DialogueFeed>();dialogueFeed.box=dialogue;dialogueFeed.BindOffice(session);
                var all=new List<Exchange>();
                Pump Run(){var pump=Drain(session);pump.Run();all.AddRange(pump.exchanges);return pump;}
                session.LoadPatients();var list=Run();
                Check(list.exchanges.Count==1&&list.exchanges[0].path=="/patients"&&session.Patients.Length==12,"actual patient list returns all twelve synthetic scenarios through the production coroutine");
                var available=session.Patients.Where(entry=>(entry.status=="ready"||entry.status=="needs_review")&&!string.IsNullOrEmpty(entry.patientId)&&!string.IsNullOrEmpty(entry.procedureId)).ToArray();
                Check(available.Length==8,"patient list exposes all eight available canonical synthetic subjects");
                // An active transport so the production auto-connect can begin (its HTTP is never pumped: no provider).
                liveVoiceObject=new GameObject("EncounterAutoVoiceFixture");var liveVoice=liveVoiceObject.AddComponent<QuestJarvisVoice>();session.voice=liveVoice;
                var patientLabels=new HashSet<string>();int interviews=0;
                foreach(var entry in available)
                {
                    session.StartPatient(entry.patientId);var started=Run();
                    Check(started.exchanges.Count==2&&started.exchanges[0].path=="/patients/"+entry.patientId+"/case"&&started.exchanges[1].path=="/interviews"&&started.exchanges[1].body==JsonUtility.ToJson(new PatientRequest{patientId=entry.patientId}),"case eligibility is checked before the exact subject's interview is created: "+entry.patientId);
                    if(Authored(entry.patientId)==null)
                    {
                        Check(session.NoInterview&&session.State==null&&session.Status.Contains("Surgery coming soon")&&liveVoice.Status!="connecting","a patient without an authored interview says surgery is coming soon and connects no voice: "+entry.patientId);
                        continue;
                    }
                    interviews++;
                    Check(session.State!=null&&session.State.patientId==entry.patientId&&session.State.phase=="interview"&&QuestJarvisVoice.ValidInterviewId(session.State.encounterId)&&session.AuthoredProcedureId==entry.procedureId,"actual interview adopts each selected subject and its case surgery: "+entry.patientId);
                    bool parent=session.State.speaker=="parent";
                    string opener=Get<string>(session,"greeting");
                    Check(!string.IsNullOrWhiteSpace(opener)&&dialogue.SpeakerLabel==(parent?session.State.speakerName+" · Parent":session.State.patientName+" · Patient")&&dialogue.FullText==opener.Trim(),"dialogue box shows the opening line by speaker role: "+entry.patientId+" -> "+dialogue.SpeakerLabel);
                    if(entry.patientId=="patient-demo-pediatric-asthma")
                        Check(parent&&session.State.patientName=="Theo Abernathy"&&session.State.speakerName=="Laura Abernathy"&&dialogue.SpeakerLabel=="Laura Abernathy · Parent","pediatric interview keeps Laura as the parent speaker for Theo, in the dialogue box too");
                    // Requirement: every playable patient is seated as soon as the interview starts, from service demographics.
                    var seated=session.patient;
                    Check(session.State.patientAge>0&&(session.State.patientSex=="female"||session.State.patientSex=="male")&&session.State.speakerAge>=18&&(session.State.speakerSex=="female"||session.State.speakerSex=="male"),"interview start carries seated patient and speaker demographics: "+entry.patientId);
                    Check(seated.Patient&&seated.Patient.activeSelf&&seated.Speaker&&seated.Speaker.activeSelf&&(parent||seated.Patient==(session.State.patientSex=="female"?seated.female:seated.male)),"a seated patient avatar appears when the interview starts: "+entry.patientId);
                    if(parent)
                        Check(seated.Companion&&seated.Companion.activeSelf&&seated.Speaker==seated.Companion&&seated.Patient.transform.localScale.x<seated.Companion.transform.localScale.x*.85f,"pediatric interview seats a scaled child with the speaking parent beside them: "+entry.patientId);
                    else Check(!seated.Companion&&seated.Speaker==seated.Patient,"adult patient speaks for themself from the patient chair: "+entry.patientId);
                    // Voice auto-connects through the interview's own connection route, microphone muted.
                    Check(liveVoice.Status=="connecting"&&liveVoice.CoachSessionId==session.State.encounterId&&Get<bool>(liveVoice,"interviewMode")&&Get<string>(liveVoice,"encounterRole")=="patient"&&liveVoice.MicrophoneMuted,"interview start auto-connects the patient voice with the microphone muted: "+entry.patientId);
                    Check(session.AwaitingPatient&&session.Round==null,"round 1 waits for the patient's opening line: "+entry.patientId);
                    var mouth=liveVoice.Speaker;
                    Check(mouth&&mouth==seated.MouthSource&&mouth.transform.IsChildOf(seated.Speaker.transform)&&mouth.transform.parent.name=="JawPivot"&&mouth.spatialBlend==1&&!mouth.mute&&mouth.volume>0&&mouth.enabled&&mouth.gameObject.activeInHierarchy,"patient voice AudioSource is a spatial, audible source at the speaking avatar's mouth: "+entry.patientId);
                    string label=EncounterContract.SpeakerLabel(session.State,"patient");
                    Check(session.LastSpeaker==label&&!label.Contains("Jarvis")&&!label.Contains("/")&&label.StartsWith(parent?session.State.speakerName:session.State.patientName,StringComparison.Ordinal),"opening line is labelled with the actual patient or parent speaker: "+entry.patientId);
                    patientLabels.Add(label);
                    if(entry.patientId=="patient-demo-pediatric-asthma")Check(label=="Laura Abernathy · Parent of Theo","Theo's interview is labelled as coming from his mother Laura");
                }
                Check(interviews>=7&&patientLabels.Count==interviews,"each authored interview has a distinct speaker label ("+interviews+" interviews)");
                session.ToggleVoice();Check(!session.VoiceEnabled&&liveVoice.Status=="disconnected","explicit Voice off disconnects the patient voice");
                session.StartPatient(EncounterContract.MalePatientId);Run();
                Check(session.State!=null&&liveVoice.Status!="connecting"&&!session.VoiceEnabled&&(session.RoundVisible||liveVoice.PlaybackActive&&session.AwaitingPatient),"with Voice off a new interview does not connect; round 1 shows at once or after a bundled opener: "+liveVoice.Status);
                liveVoice.Disconnect();Call(session,"Update");
                Check(session.RoundVisible&&session.Round.number==1,"with no voice playing, round 1 shows");
                session.ToggleVoice();Check(session.VoiceEnabled&&liveVoice.Status=="connecting","Voice on reconnects the current interview's patient voice");
                liveVoice.Disconnect();session.voice=null;
                var unavailable=session.Patients.Where(entry=>entry.status!="connect"&&entry.status!="ready"&&entry.status!="needs_review").ToArray();
                Check(unavailable.Length==2,"patient list keeps both unavailable synthetic record cases visible as unavailable");
                foreach(var entry in unavailable)
                {
                    session.StartPatient(entry.patientId);var rejected=Run();
                    Check(rejected.exchanges.Count==1&&rejected.exchanges[0].path=="/patients/"+entry.patientId+"/case"&&session.State==null,"unavailable case is rejected before interview POST: "+entry.patientId);
                }
                var connect=session.Patients.Where(entry=>entry.status=="connect").ToArray();
                Check(connect.Length==2&&connect.All(entry=>string.IsNullOrEmpty(entry.patientId)),"Connect scenarios retain no canonical subject before consent");
                foreach(var entry in connect)
                {
                    session.StartPatient(entry.patientId);var rejected=Run();
                    Check(rejected.exchanges.Count==0&&session.State==null,"subjectless Connect scenario cannot issue a case lookup or interview POST: "+entry.scenarioId);
                }
                // Full interviews against the actual service: an adult whose plan pick is wrong, then the pediatric parent case.
                voice=fixture.AddComponent<QuestJarvisVoice>();
                RunInterview(session,voice,dialogue,endpoint,EncounterContract.FemalePatientId,true,connections,Run);
                RunInterview(session,voice,dialogue,endpoint,"patient-demo-pediatric-asthma",false,connections,Run);
                RunSkip(session,voice,endpoint,Run);
                // Stale responses from an abandoned patient never reach the next one.
                session.voice=null;session.StartPatient(EncounterContract.MalePatientId);Run();string maleId=session.State.encounterId;
                session.Choose("A");var stale=Drain(session);Check(stale.Step(),"old-patient answer request begins");
                session.StartPatient(EncounterContract.FemalePatientId);stale.Run();Run();
                Check(session.State.patientId==EncounterContract.FemalePatientId&&session.State.encounterId!=maleId&&session.Round!=null&&session.Round.number==1&&session.Findings.Count==0,"a new patient rejects the stale answer and starts at round 1");
                // Voice transport for the interview: identity, then the interview's own connection route; never Jarvis.
                voice.Disconnect();voice.ConfigureEndpoint(endpoint);
                Set(voice,"encounterMode",true);Set(voice,"interviewMode",true);Set(voice,"encounterRole","patient");Set(voice,"patientId",session.State.patientId);Property(voice,"CoachSessionId",session.State.encounterId);
                var begin=new Pump((IEnumerator)Call(voice,"BeginConversation",Get<int>(voice,"generation")));begin.Run();all.AddRange(begin.exchanges);
                Check(begin.exchanges.Count==2&&begin.exchanges[0].path=="/interviews/"+session.State.encounterId&&begin.exchanges[1].path=="/interviews/"+session.State.encounterId+"/connection"&&voice.Status=="error"&&voice.LastError.Contains("voice_unconfigured"),"interview voice validates the interview then requests its patient connection; missing credentials fail visibly before WSS");
                Set(voice,"prompt","server prompt");Set(voice,"firstMessage","server opening");Set(voice,"encounterVoiceId","patient-voice");Set(voice,"encounterMode",true);Set(voice,"patientId",session.State.patientId);Property(voice,"CoachSessionId",session.State.encounterId);
                var init=JsonUtility.FromJson<VoiceInit>((string)Call(voice,"BuildInitiation","patient"));
                Check(init.conversation_config_override.agent.prompt.prompt=="server prompt"&&init.conversation_config_override.agent.first_message=="server opening"&&init.conversation_config_override.tts.voice_id=="patient-voice"&&init.dynamic_variables.encounter_id==session.State.encounterId&&init.dynamic_variables.mode=="patient","the patient agent starts with the interview's prompt, opening line and voice overrides");
                // No attending connection, encounter or Jarvis route is attempted anywhere in the office.
                Check(all.Count>40&&!all.Any(exchange=>exchange.path.Contains("/attending")||exchange.path.StartsWith("/encounters",StringComparison.Ordinal)||exchange.path.StartsWith("/jarvis",StringComparison.Ordinal)),"the office never calls /encounters, an attending route or a Jarvis connection ("+all.Count+" exchanges)");
            }
            finally
            {
                if(voice)voice.Disconnect();if(fixture)UnityEngine.Object.DestroyImmediate(fixture);if(liveVoiceObject)UnityEngine.Object.DestroyImmediate(liveVoiceObject);
                foreach(var created in connections)foreach(var name in new[]{"Socket","Cancel","SendSignal"})if(created.GetType().GetField(name).GetValue(created) is IDisposable disposable)disposable.Dispose();
                if(server!=null){if(!server.HasExited)server.Kill();server.WaitForExit(5000);server.Dispose();}
            }
        }
        // One full interview through the production session: opening line gated on speech end, an unclear spoken answer,
        // a recognised spoken answer, taps for the rest, a direction + clinician turn sent after every pick and the next
        // round withheld until the patient finishes, then the scorecard, the coach session and the handoff ticket.
        static void RunInterview(NativeEncounterSession session,QuestJarvisVoice voice,Scalpal.Shell.DialogueBox dialogue,string endpoint,string patient,bool wrongPlan,List<object> connections,Func<Pump> run)
        {
            var authored=Authored(patient);Check(authored!=null&&authored.rounds.Length>=6,"committed interview content exists: "+patient);
            session.voice=null;session.StartPatient(patient);run();
            session.voice=voice;voice.MicrophoneMuted=false;Call(session,"ConnectVoice");
            Check(voice.MicrophoneMuted&&Get<bool>(voice,"interviewMode"),"connecting the interview voice mutes the microphone: "+patient);
            // A live patient connection, with no provider: messages queue on the fixture socket.
            int epoch=Get<int>(voice,"generation")+1;voice.Disconnect();Set(voice,"generation",epoch);Property(voice,"Status","connected");
            var connection=NewConnection(voice,epoch);connections.Add(connection);
            var outgoing=(ConcurrentQueue<string>)connection.GetType().GetField("Outgoing").GetValue(connection);
            Call(session,"AwaitPatient");
            float now=Time.realtimeSinceStartup;
            Check(!(bool)Call(session,"PatientDone",now)&&session.Round==null,"the round waits while the patient has not spoken yet: "+patient);
            Speak(session,voice);
            Check(session.RoundVisible&&session.Round.number==1&&session.Round.of==authored.rounds.Length,"round 1 appears once the opening line has been spoken: "+patient);
            Call(dialogue.GetComponent<Scalpal.Shell.DialogueFeed>(),"Update");
            Check(dialogue.ChoicesVisible&&dialogue.ChoiceRows.Count==4&&dialogue.ChoiceHeader=="1/"+authored.rounds.Length,"the dialogue box shows round 1's four choices with a small 1/"+authored.rounds.Length+" counter: "+patient);
            // Hold-to-answer, unclear: the fixture hears silence as "um I'm not sure".
            Call(session,"SubmitRecordedAnswer",new float[16000],16000);var unclear=run();
            Check(unclear.exchanges.Single().path=="/interviews/"+session.State.encounterId+"/answer"&&session.Round.number==1&&session.AnswerNote.StartsWith("Say A, B, C or D, or tap one.",StringComparison.Ordinal)&&session.AnswerNote.Contains("um I'm not sure")&&outgoing.IsEmpty,"422 unclear_answer re-asks with what was heard and sends the patient nothing: "+patient);
            Call(dialogue.GetComponent<Scalpal.Shell.DialogueFeed>(),"Update");
            Check(dialogue.ChoiceNote==session.AnswerNote,"the re-ask note shows under the choices: "+patient);
            int findings=0;var keys=new List<string>();
            for(int i=0;i<authored.rounds.Length;i++)
            {
                var round=session.Round;var spec=authored.rounds.Single(item=>item.id==round.roundId);
                Check(round.number==i+1&&voice.MicrophoneMuted,"round "+(i+1)+" is current and the microphone is muted: "+patient);
                string key=spec.stage=="plan"&&wrongPlan?spec.choices.First(choice=>choice.grade=="wrong").key:spec.choices.Single(choice=>choice.grade=="correct").key;
                var chosen=spec.choices.Single(choice=>choice.key==key);if(chosen.finding!=null&&!string.IsNullOrEmpty(chosen.finding.text))findings++;
                int turns=session.TurnsSent;
                Pump answered;
                if(i==0&&key=="B") { Call(session,"SubmitRecordedAnswer",Tone(16000,1f,.5f),16000);answered=run(); }
                else if(i==0) { Call(session,"SubmitRecordedAnswer",Tone(16000,1f,.5f),16000);var spoken=run();
                    Check(spoken.exchanges.Single().body.Contains("\"mimeType\":\"audio/wav\"")&&session.Round.number==2&&session.TurnsSent==turns+1,"a recognised spoken answer (\"option b\") picks B: "+patient);
                    keys.Add("B");Drop(outgoing);Speak(session,voice);continue; }
                else { session.Choose(key);answered=run(); }
                keys.Add(key);
                Check(answered.exchanges.Single().path=="/interviews/"+session.State.encounterId+"/answer"&&(i==0||answered.exchanges.Single().body=="{\"key\":\""+key+"\"}"),"pick "+(i+1)+" posts to the interview answer route: "+patient);
                Check(dialogue.SpeakerLabel=="You"&&dialogue.FullText.StartsWith(key+") ",StringComparison.Ordinal),"the pick is shown as the learner's line: "+patient+" got "+dialogue.SpeakerLabel+": "+dialogue.FullText+" key "+key+" round "+(i+1)+" status "+session.Status+" note "+session.AnswerNote+" exch "+answered.exchanges.Count);
                var messages=outgoing.ToArray();
                var direction=JsonUtility.FromJson<TextFrame>(messages[0]);var clinician=JsonUtility.FromJson<TextFrame>(messages[1]);
                bool last=i==authored.rounds.Length-1;
                Check(messages.Length==2&&session.TurnsSent==turns+1&&direction.type=="contextual_update"&&direction.text.StartsWith("[DIRECTION] ",StringComparison.Ordinal)&&clinician.type=="user_message"&&clinician.text=="[CLINICIAN] "+round.choices.Single(choice=>choice.key==key).text,"pick "+(i+1)+" sends [DIRECTION] as a contextual update, then [CLINICIAN] as the user message: "+patient);
                if(last&&!string.IsNullOrEmpty(authored.closingLine))Check(direction.text.Contains("Then close with")&&direction.text.Contains(authored.closingLine.Trim()),"the last direction carries the closing line: "+patient);
                Drop(outgoing);
                Check(session.Round==null&&session.AwaitingPatient&&!session.SurgeryReady,"after pick "+(i+1)+" nothing more is shown until the patient finishes: "+patient);
                Speak(session,voice);
            }
            Check(session.Findings.Count==findings&&!session.Findings.Any(finding=>dialogue.FullText.Contains(finding.text)),"exam and test results go to the findings panel, not the dialogue: "+findings+" findings: "+patient);
            var score=session.Score;
            Check(session.SurgeryReady&&session.State.phase=="scored"&&score.kind=="interview"&&score.max==100&&score.rounds.Length==authored.rounds.Length&&score.sections.Length>0&&score.feedback.Length>0&&string.IsNullOrEmpty(score.spoken),"the scorecard (total, sections, rounds, feedback) is on screen only: "+patient);
            Check(score.procedureChosenCorrectly==!wrongPlan&&score.procedureId==session.AuthoredProcedureId&&score.rounds.Select(r=>r.picked.key).SequenceEqual(keys),"scorecard rounds record the picks and the case surgery stays authored: "+patient);
            Call(session,"Transcript","agent","Okay, thank you.");var mirrored=run();
            Check(mirrored.exchanges.Single().path=="/interviews/"+session.State.encounterId+"/transcript"&&mirrored.exchanges.Single().body.Contains("\"speaker\":\"patient\""),"the patient's lines are mirrored to the interview transcript: "+patient);
            // Scrub in: the coach session carries the interview; the handoff ticket is built from its scorecard.
            Check(session.TryPrepareHandoff(out var handoff,out var reason),"scored interview prepares the OR handoff: "+reason);
            var liveCase=JsonUtility.FromJson<Scalpal.Exercises.Data.SurgicalCase>(DirectText(endpoint,"GET","/patients/"+patient+"/case",null));
            var liveInterview=EncounterContract.ReadOffice(DirectText(endpoint,"GET",EncounterContract.OfficePath(handoff.encounterId),null),handoff.encounterId);
            var liveScore=EncounterContract.ReadOffice(DirectText(endpoint,"GET",EncounterContract.OfficePath(handoff.encounterId)+"/score",null),handoff.encounterId);
            Check(EncounterSurgeryBinding.Validate(handoff,liveCase,liveInterview,liveScore,out reason),"the live interview and score confirm the handoff: "+reason);
            var tampered=JsonUtility.FromJson<EncounterReply>(JsonUtility.ToJson(liveScore));tampered.scorecard.total++;
            Check(!EncounterSurgeryBinding.Validate(handoff,liveCase,liveInterview,tampered,out _),"a changed live interview score refuses the handoff");
            string create=EncounterSurgeryBinding.CoachCreateJson(handoff,"virtual");
            Check(create.Contains("\"encounterId\":\""+session.State.encounterId+"\"")&&create.Contains("\"mode\":\"virtual\""),"scrub in posts the interview id as encounterId");
            var coach=JsonUtility.FromJson<CoachCreated>(DirectText(endpoint,"POST","/coach/sessions",create));
            Check(coach.sessionId.StartsWith("coach-",StringComparison.Ordinal)&&coach.systemPrompt.Contains("Pre-op interview score "+score.total+"/100")&&coach.snapshot.procedureId==score.procedureId,"the coach session is created with encounterId=interviewId and carries the interview result");
            var ticket=Scalpal.Handoff.HandoffRun.Begin(session.State,score,endpoint);
            try
            {
                Check(ticket.encounterId==session.State.encounterId&&ticket.procedureId==score.procedureId&&ticket.scorecard.carryoverItems==score.carryoverItems&&ticket.escalated==wrongPlan
                    &&ticket.learnerProcedure==EncounterContract.Picked(score,"plan"),"the handoff ticket carries the interview's procedure, carryover risks and (for a wrong plan) the escalation: "+patient);
            }
            finally { Scalpal.Handoff.HandoffRun.Clear(); }
            voice.Disconnect();
        }
        // Skip to surgery from the explore card (selection flag -> case -> Theatre card, no interview) and from the office pause
        // (leave a running interview): the case's procedure, chart-flag Time-Out risks, a coach session without encounterId,
        // and a recap that reads Skipped.
        static void RunSkip(NativeEncounterSession session,QuestJarvisVoice voice,string endpoint,Func<Pump> run)
        {
            var flowObject=new GameObject("SkipHandoffFlowFixture");GameObject nativeObject=null;
            var previousTicket=Scalpal.Handoff.HandoffRun.Current;
            try
            {
                var flow=flowObject.AddComponent<Scalpal.Handoff.HandoffFlow>();
                var card=UnityEngine.Object.Instantiate(Resources.Load<Scalpal.Handoff.HandoffCard>("HandoffCard"),flowObject.transform);
                Set(flow,"card",card);Set(flow,"nextHealth",float.MaxValue);Set(flow,"focused",true);
                const string patient=EncounterContract.FemalePatientId;
                var kase=JsonUtility.FromJson<Scalpal.Exercises.Data.SurgicalCase>(DirectText(endpoint,"GET","/patients/"+patient+"/case",null));
                // Explore: the detail card's Skip to surgery stages the selection with the skip flag for the office scene.
                Check(Scalpal.Shell.ShellTransition.TryStageSelection(patient,endpoint,true)&&Scalpal.Shell.ShellTransition.TryConsumeSelection(out var selection)&&selection.skipToSurgery&&selection.patientId==patient,"explore's Skip to surgery stages the patient with the skip flag");
                session.voice=null;session.StartPatient(patient,true);var skipped=run();
                Check(skipped.exchanges.Count==1&&skipped.exchanges[0].path=="/patients/"+patient+"/case"&&session.Skipped&&session.State.encounterId==""&&session.Round==null,"skip from explore loads only the case: no interview is created");
                // The isolated fixture has no paired bridge; give the run the office's captured shared attempt, as production does.
                Set(session,"sharedSessionId","skip-session");Set(session,"sharedAttemptId","skip-attempt");
                Check(Scalpal.Handoff.HandoffFlow.OpenSkipped(session),"the skipped run opens the theatre handoff");
                var ticket=Scalpal.Handoff.HandoffRun.Current;
                Check(ticket.skipped&&ticket.encounterId==""&&ticket.procedureId==kase.procedureId&&ticket.patientId==patient&&EncounterContract.IsSkipped(ticket.scorecard)&&ticket.scorecard.diagnosisResult=="skipped"&&!ticket.escalated,"the skipped ticket targets the case's procedure with no interview and diagnosis skipped");
                var risks=Scalpal.Handoff.HandoffFlow.ReviewRisks(ticket.scorecard);
                Check(risks.Length>0&&risks.Length==kase.brief.flags.Select(flag=>flag.type).Distinct().Count()&&risks.All(risk=>risk.status=="chart"),"Time-Out still reviews the case's chart risks: "+risks.Length);
                Set(flow,"nextRefresh",0f);Call(flow,"Update");
                Check(Get<string>(flow,"phase")=="theatre"&&Get<string>(card,"heading")=="To theatre"&&Get<string[]>(card,"actions")[1]=="Virtual OR (VR)","skip reaches the Theatre card (AR/VR choice)");
                // Coach session: no encounterId at all.
                nativeObject=new GameObject("SkipCoachFixture");nativeObject.SetActive(false);
                var native=nativeObject.AddComponent<Scalpal.Quest.NativeCaseSession>();
                string coachJson=native.CoachRequestJson();
                Check(!coachJson.Contains("encounterId")&&coachJson.Contains("\"patientId\":\""+patient+"\"")&&coachJson.Contains("\"mode\":\"virtual\""),"the skipped coach request carries patient and mode and no encounterId: "+coachJson);
                var coach=JsonUtility.FromJson<CoachCreated>(DirectText(endpoint,"POST","/coach/sessions",coachJson));
                Check(coach.sessionId.StartsWith("coach-",StringComparison.Ordinal)&&coach.snapshot.procedureId==kase.procedureId&&!coach.systemPrompt.Contains("Pre-op interview score"),"the coach session is created for the case's procedure without interview carryover");
                // Recap: Clinical reasoning reads Skipped, not zero and not missing data.
                Check(ticket.sharedSessionId=="skip-session"&&ticket.attemptId=="skip-attempt","the skipped run keeps the office's shared attempt");
                var result=Scalpal.Recap.RecapSessionIntegration.FromHandoff(ticket,kase,"skip-session","skip-attempt");
                string clinical=Scalpal.Recap.RecapPanel.Clinical(result);
                Check(result.diagnosisSkipped&&!result.diagnosisAvailable&&clinical.Contains("Skipped")&&!clinical.Contains("Not available")&&!clinical.Contains("0 / "),"recap shows Clinical reasoning: Skipped");
                Scalpal.Handoff.HandoffRun.Clear();
                // Office pause: Skip to surgery leaves a running interview for the same Theatre card.
                session.StartPatient(patient);run();
                Check(session.State.phase=="interview"&&session.RoundVisible,"precondition: an interview is running");
                Set(session,"sharedSessionId","skip-session");Set(session,"sharedAttemptId","skip-attempt");
                int before=0;
                Check(session.SkipToSurgery()&&session.Skipped&&session.Round==null&&!session.AwaitingPatient&&Scalpal.Handoff.HandoffRun.Current!=null&&Scalpal.Handoff.HandoffRun.Current.skipped
                    &&Scalpal.Handoff.HandoffRun.Current.procedureId==kase.procedureId&&Get<string>(flow,"phase")=="theatre","skip from the office pause leaves the interview for the Theatre card");
                Check(Drain(session).exchanges.Count==before,"skipping sends nothing more to the interview");
            }
            finally
            {
                Scalpal.Handoff.HandoffRun.Clear();if(previousTicket!=null)typeof(Scalpal.Handoff.HandoffRun).GetProperty("Current").GetSetMethod(true).Invoke(null,new object[]{previousTicket});
                UnityEngine.Object.DestroyImmediate(flowObject);if(nativeObject)UnityEngine.Object.DestroyImmediate(nativeObject);
            }
        }
        // The patient speaks one reply: agent response, audio, then drained playback; the round gate then opens.
        static void Speak(NativeEncounterSession session,QuestJarvisVoice voice)
        {
            float now=Time.realtimeSinceStartup;
            Call(voice,"SetMode","speaking");Property(voice,"AgentResponses",voice.AgentResponses+1);
            Check(!(bool)Call(session,"PatientDone",now),"the round stays hidden while the patient is speaking");
            Call(voice,"SetMode","listening");
            Check(!(bool)Call(session,"PatientDone",now+.1f),"a brief gap between audio chunks does not reveal the round");
            Check((bool)Call(session,"PatientDone",now+1f),"the round is released once the reply has finished and playback drained");
            Call(session,"ReleaseRound");
        }
        static void Drop(ConcurrentQueue<string> queue){while(queue.TryDequeue(out _)){}}
        [Serializable] sealed class TextFrame { public string type, text; }
        [Serializable] sealed class CoachCreated { public string sessionId, systemPrompt; public CoachSnapshot snapshot; }
        [Serializable] sealed class CoachSnapshot { public string procedureId; }
        static string DirectText(string endpoint,string method,string path,string body)
        {
            using(var request=new UnityWebRequest(endpoint+path,method))
            {
                request.downloadHandler=new DownloadHandlerBuffer();request.timeout=6;
                if(body!=null){request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));request.SetRequestHeader("Content-Type","application/json");}
                var operation=request.SendWebRequest();var clock=Stopwatch.StartNew();
                while(!operation.isDone){if(clock.ElapsedMilliseconds>8000)throw new InvalidOperationException("Direct fixture HTTP timed out");Thread.Sleep(5);}
                if(request.result!=UnityWebRequest.Result.Success)throw new InvalidOperationException("Direct fixture HTTP "+method+" "+path+" status="+request.responseCode+" "+request.downloadHandler.text);
                return request.downloadHandler.text;
            }
        }
        static InterviewRoundView FixtureRound(int number)=>new InterviewRoundView{roundId="round"+number,number=number,of=7,stage="history",prompt="What do you do next?",choices=new[]{"A","B","C","D"}.Select(key=>new InterviewChoiceView{key=key,text="Choice "+key}).ToArray()};
        static float[] Tone(int rate,float seconds,float amplitude)=>Enumerable.Range(0,(int)(rate*seconds)).Select(i=>amplitude*Mathf.Sin(2*Mathf.PI*220*i/rate)).ToArray();
        static EncounterScore InterviewScore(int feedback)=>new EncounterScore{kind="interview",total=64,max=100,grade="Developing",patientId=EncounterContract.FemalePatientId,procedureId="open_appendectomy",
            sections=new[]{new EncounterScoreSection{id="history",label="History",score=20,max=30},new EncounterScoreSection{id="plan",label="Plan",score=0,max=15}},
            rounds=new[]{
                new InterviewRoundResult{stage="history",prompt="Where to start?",points=10,max=10,picked=new InterviewPickedChoice{key="A",text="Ask where it hurts",grade="correct"},best=new InterviewPickedChoice{key="A",text="Ask where it hurts"}},
                new InterviewRoundResult{stage="exam",prompt="Next?",points=0,max=15,picked=new InterviewPickedChoice{key="B",text="Order a CT first",grade="wrong"},best=new InterviewPickedChoice{key="C",text="Examine the abdomen"}},
                new InterviewRoundResult{stage="diagnosis",prompt="Diagnosis?",points=25,max=25,picked=new InterviewPickedChoice{key="C",text="Acute appendicitis",grade="correct"},best=new InterviewPickedChoice{key="C",text="Acute appendicitis"}},
                new InterviewRoundResult{stage="plan",prompt="Plan?",points=0,max=15,picked=new InterviewPickedChoice{key="D",text="Observe overnight",grade="wrong"},best=new InterviewPickedChoice{key="A",text="Open appendectomy"}}},
            carryoverItems=new EncounterCarryoverItem[0],feedback=Enumerable.Range(0,feedback).Select(n=>"Feedback item "+n).ToArray()};
        static Pump Drain(NativeEncounterSession session)=>new Pump((IEnumerator)Call(session,"Drain"));
        static object NewConnection(QuestJarvisVoice voice,int epoch)
        {
            var type=typeof(QuestJarvisVoice).GetNestedType("Connection",BindingFlags.NonPublic);var connection=Activator.CreateInstance(type,new object[]{epoch});type.GetField("Open").SetValue(connection,true);Set(voice,"active",connection);return connection;
        }
        static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private).Invoke(target,args);
        static void Set(object target,string name,object value)=>target.GetType().GetField(name,Private).SetValue(target,value);
        static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Private).GetValue(target);
        static T FieldOf<T>(object target,string name)=>(T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(target);
        static void Property(object target,string name,object value)=>target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target,new[]{value});
        static string Quote(string path)=>"\""+path.Replace("\"","\\\"")+"\"";
        static void Check(bool value,string reason){if(!value)throw new InvalidOperationException("Encounter office validation: "+reason);checks++;}
        sealed class Exchange {public string path,query,body;}
        sealed class Pump
        {
            readonly Stack<IEnumerator> stack=new Stack<IEnumerator>();AsyncOperation waiting;public readonly List<Exchange> exchanges=new List<Exchange>();
            public Pump(IEnumerator routine){stack.Push(routine);}
            public bool Step()
            {
                if(waiting!=null&&!waiting.isDone)return true;waiting=null;
                while(stack.Count>0)
                {
                    var top=stack.Peek();if(!top.MoveNext()){stack.Pop();continue;}if(top.Current is IEnumerator nested){stack.Push(nested);continue;}
                    if(top.Current is UnityWebRequestAsyncOperation operation){var request=operation.webRequest;var uri=new Uri(request.url);exchanges.Add(new Exchange{path=uri.AbsolutePath,query=uri.Query,body=request.uploadHandler==null?null:Encoding.UTF8.GetString(request.uploadHandler.data)});}
                    waiting=top.Current as AsyncOperation;return true;
                }
                return false;
            }
            public void Run(){var clock=Stopwatch.StartNew();while(Step()){if(clock.ElapsedMilliseconds>15000)throw new InvalidOperationException("Encounter coroutine timed out");Thread.Sleep(5);}}
        }
    }
}
