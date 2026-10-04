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
            Check(rig.head.transform.parent==rig.origin&&rig.left.parent==rig.origin&&rig.right.parent==rig.origin,"head and controllers share one floor tracking origin");
            Check(panel&&panel.session==session&&panel.options.Length==4&&panel.keyboard&&panel.assessment&&panel.chart&&panel.response&&panel.draft,"visual fallback has paged questions, findings and editable assessment");
            Check(session.patient.female&&session.patient.male&&!session.patient.male.activeSelf,"both adult synthetic character presentations bind; only one starts active");
            foreach(var patient in new[]{session.patient.female,session.patient.male})
                Check(patient.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="HeadPivot")&&patient.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="JawPivot"),"stylized head and jaw animation nodes exist");
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
            Check(!UnityEngine.Object.FindFirstObjectByType<Scalpal.Quest.NativeCaseSession>()&&!UnityEngine.Object.FindFirstObjectByType<Scalpal.Instruments.TrainingTarget>(),"encounter scene does not instantiate surgery progression or scored tissue targets");
            var buttons=UnityEngine.Object.FindObjectsByType<EncounterOfficeButton>(FindObjectsInactive.Include,FindObjectsSortMode.None);
            foreach(var command in new[]{"patient","page","option","voice","stop","attending","submit","field","key","refresh","summary"})
                Check(buttons.Any(button=>button.command==command&&button.GetComponent<Collider>()&&button.panel==panel),"ray-accessible control exists: "+command);
            panel.Act("field","diagnosis");panel.Act("key","clear");panel.Act("key","a");panel.Act("key","space");panel.Act("key","b");panel.Act("key","back");
            Check(session.Draft.diagnosis=="a ","ray keyboard supports letter, space, clear and backspace editing");
            panel.Act("page","assessment");panel.Act("field","differential");panel.Act("option","Ectopic pregnancy");panel.Act("option","Ureteric stone");panel.Act("option","Ectopic pregnancy");
            Check(session.Draft.differential.SequenceEqual(new[]{"Ureteric stone"}),"headset choices toggle learner-selected differential without prescribing one");
            session.Draft.procedure=string.Join(" ",Enumerable.Range(0,80).Select(n=>"step"+(char)('a'+n%26))).Substring(0,400);panel.Act("field","procedure");panel.Refresh();
            string finalDraftPage=panel.draft.text;Check(finalDraftPage.Split('\n').Length<=4&&finalDraftPage.Contains("step"),"400-character typed plan is paged into a readable three-line field region");
            panel.Act("draft_previous","");Check(panel.draft.text!=finalDraftPage&&panel.draft.text.Split('\n').Length<=4,"draft back exposes earlier long assessment text without truncating stored plan");
            for(int i=0;i<6;i++)panel.Act("draft_previous","");Check(panel.draft.text.Contains("stepa")&&session.Draft.procedure.Length==400,"learner can review beginning of full stored 400-character plan");
            Property(session,"LastResponse",string.Join("\n",Enumerable.Range(0,28).Select(n=>"Authored response line "+n)));panel.Refresh();
            Check(panel.response.text.Split('\n').Length<=7&&!panel.response.text.Contains("line 20"),"long returned patient/summary text stays within paged response region");panel.Act("response_next","");
            Check(panel.response.text.Contains("line 6")&&!panel.response.text.Contains("line 0"),"ray paging exposes next returned response lines");
            Property(session,"Score",new EncounterScore{total=23,max=100,grade="Needs practice",feedback=Enumerable.Range(0,26).Select(n=>"Feedback item "+n).ToArray()});panel.Refresh();
            Check(panel.chart.text.Split('\n').Length<=11&&panel.draft.text.Split('\n').Length<=3,"full score feedback is paged and assessment panel does not overflow controls");
            var fittedTexts=UnityEngine.Object.FindObjectsByType<EncounterOfficeText>(FindObjectsInactive.Include,FindObjectsSortMode.None);
            Check(fittedTexts.Length==UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include,FindObjectsSortMode.None).Length,"every scene text has a measured bounds fitter");
            foreach(var fit in fittedTexts)
            {
                fit.Fit();var measured=fit.MeasuredSize();
                Check(measured.x<=fit.maximumWidth+.002f&&measured.y<=fit.maximumHeight+.002f,"actual generated TextMesh bounds fit their world-space region: "+fit.name);
                Check(fit.GetComponent<Renderer>().sharedMaterial.shader.name=="Scalpal/Encounter Office/World Text"&&fit.GetComponent<Renderer>().sharedMaterial.renderQueue==3020,"depth-tested text renders above glass and buttons");
            }
            Check(before.SequenceEqual(EditorBuildSettings.scenes.Select(s=>s.path+s.enabled)),"validation preserves surgery build scene settings");
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
        static void ValidateContracts()
        {
            Check(!EncounterContract.HasError(JsonUtility.FromJson<EncounterReply>("{\"result\":\"Recorded.\"}")) && EncounterContract.HasError(JsonUtility.FromJson<EncounterReply>("{\"error\":{\"code\":\"invalid_topic\",\"message\":\"Unknown topic\"}}")),"JsonUtility absent/empty error object is not a service error; nonempty error content is");
            Check(EncounterContract.ToolAllowed("patient","interview","answer"),"patient interviews use existing answer tool");
            Check(!EncounterContract.ToolAllowed("patient","interview","record_assessment")&&!EncounterContract.ToolAllowed("attending","attending","answer"),"roles cannot route each other's tools");
            Check(!EncounterContract.ToolAllowed("patient","attending","examine")&&!EncounterContract.ToolAllowed("attending","scored","record_assessment"),"mutations stop at phase boundaries");
            Check(EncounterContract.ToolAllowed("attending","scored","get_encounter_summary"),"attending can explain grounded gathered actions after scoring");
            var state=new EncounterState{encounterId="enc-test000",patientId=EncounterContract.FemalePatientId,version=3,patientName="Priya Ramaswamy",phase="interview",exams=new[]{new EncounterItem{label="abdomen",finding="Authored finding"}},tests=new[]{new EncounterItem{label="CBC",result="Returned result"}}};
            Check(EncounterContract.StateMatches(state,"enc-test000",state.patientId,3)&&!EncounterContract.StateMatches(state,"enc-other00",state.patientId,3)&&!EncounterContract.StateMatches(state,"enc-test000",EncounterContract.MalePatientId,3)&&!EncounterContract.StateMatches(state,"enc-test000",state.patientId,4),"state adoption checks id, patient and monotonic version");
            var chart=EncounterContract.Chart(state);Check(chart.Contains("Authored finding")&&chart.Contains("Returned result")&&!chart.Contains("appendicitis"),"chart displays only returned gathered findings, with no local diagnosis facts");
            Check(EncounterContract.Chart(null).Contains("Start a synthetic encounter"),"offline service failure does not invent findings");
        }
        static void ValidateExchange()
        {
            Process server=null;GameObject fixture=null;QuestJarvisVoice voice=null;object connection=null;var connections=new List<object>();
            try
            {
                string repo=Path.GetFullPath(Path.Combine(Application.dataPath,"../../.."));string service=Path.Combine(repo,"services/preop");
                var info=new ProcessStartInfo("/usr/bin/env","node --import "+Quote(Path.Combine(service,"node_modules/tsx/dist/loader.mjs"))+" "+Quote(Path.Combine(repo,"scripts/quest/native-coach-check/server.ts"))) { WorkingDirectory=service,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
                server=Process.Start(info);var ready=server.StandardOutput.ReadLineAsync();if(!ready.Wait(15000))throw new InvalidOperationException("Encounter fixture did not become ready");
                const string marker="SCALPAL_COACH_TEST_ENDPOINT=";string line=ready.Result??"";if(!line.StartsWith(marker,StringComparison.Ordinal))throw new InvalidOperationException("Encounter fixture failed startup");
                string endpoint=line.Substring(marker.Length);Check(new Uri(endpoint).Host=="127.0.0.1","actual service fixture is isolated loopback with external fetch disabled");
                fixture=new GameObject("EncounterOfficeFixture");fixture.SetActive(false);
                var session=fixture.AddComponent<NativeEncounterSession>();session.baseUrl=endpoint;
                session.StartPatient(EncounterContract.FemalePatientId);Drain(session).Run();
                Check(session.State!=null&&session.State.patientId==EncounterContract.FemalePatientId&&session.State.patientName=="Priya Ramaswamy"&&session.State.phase=="interview","actual create response binds female40 catalog persona; status="+session.Status+" name="+session.State?.patientName+" id="+session.State?.patientId+" phase="+session.State?.phase);
                string femaleId=session.State.encounterId;
                session.Ask("allergies");var allergy=Drain(session);allergy.Run();
                Check(allergy.exchanges.Single().path=="/encounters/"+femaleId+"/tools/answer"&&allergy.exchanges.Single().body=="{\"topic\":\"allergies\"}","fallback question uses exact authoritative encounter route and body");
                Check(session.State.historyAsked.Any(item=>item.id=="allergies")&&session.LastResponse.IndexOf("latex",StringComparison.OrdinalIgnoreCase)>=0&&!session.LastResponse.Contains("FACT for you"),"visual fallback receives case fact without provider instruction wrapper");
                session.Examine("abdomen_palpation");Drain(session).Run();
                Check(session.State.exams.Any(item=>item.id=="abdomen_palpation"&&item.finding.Contains("right lower")),"actual examination state populates findings chart");
                session.OrderTest("pregnancy_test");Drain(session).Run();
                Check(session.State.tests.Any(item=>item.id=="pregnancy_test"&&item.result.Contains("negative")),"actual authored pregnancy result appears only after order");
                voice=fixture.AddComponent<QuestJarvisVoice>();session.voice=voice;Set(voice,"generation",70);Property(voice,"Status","connected");connection=NewConnection(voice,70);connections.Add(connection);
                Call(session,"Transcript","user","Please perform ultrasound, then CT.");
                var firstTool=new QuestJarvisVoice.ToolRequest{ToolName="order_test",ToolCallId="fifo-ultrasound",ConnectionGeneration=70,ParametersJson="{\"test\":\"ultrasound\"}"};
                var secondTool=new QuestJarvisVoice.ToolRequest{ToolName="order_test",ToolCallId="fifo-ct",ConnectionGeneration=70,ParametersJson="{\"test\":\"ct_abdomen_pelvis\"}"};
                Get<HashSet<string>>(voice,"pendingTools").Add(firstTool.ToolCallId);Get<HashSet<string>>(voice,"pendingTools").Add(secondTool.ToolCallId);
                Call(session,"VoiceTool",firstTool);Call(session,"VoiceTool",secondTool);var ordered=Drain(session);ordered.Run();
                Check(ordered.exchanges.Count==3&&ordered.exchanges[0].body==firstTool.ParametersJson&&ordered.exchanges[1].body==secondTool.ParametersJson&&ordered.exchanges[2].path.EndsWith("/transcript",StringComparison.Ordinal),"priority queue preserves ultrasound then CT FIFO before transcript bookkeeping");
                Check(session.State.tests.Select(item=>item.id).SequenceEqual(new[]{"pregnancy_test","ultrasound","ct_abdomen_pelvis"}),"authoritative engine receives learner tests in the same order as provider calls");
                voice.Disconnect();session.voice=null;
                int version=session.State.version;
                session.Ask("unknown_topic");Drain(session).Run();
                Check(session.State.version==version&&session.Status.Length>0,"actual HTTP400 is visible and cannot invent or adopt new findings");
                // Simulate a lost successful transition response: service advances, local state remains interview.
                Direct(endpoint,"POST","/encounters/"+femaleId+"/attending","{}");
                session.RefreshState();var failedRecovery=Drain(session);Check(failedRecovery.Step(),"recovery GET starts before fixture transient POST failure");session.baseUrl=endpoint+"/missing";failedRecovery.Run();
                Check(session.State.phase=="attending"&&session.Role=="patient","failed attending prompt recovery does not adopt attending role with stale patient persona");
                session.voice=voice;session.StartVoice();Check(voice.Status=="disconnected"&&session.Status.Contains("correct conversation role"),"voice start is gated until the matching role prompt is loaded");session.voice=null;
                session.baseUrl=endpoint;session.RefreshState();var recovered=Drain(session);recovered.Run();
                Check(session.Role=="attending"&&session.State.phase=="attending"&&Get<string>(session,"prompt").Contains("attending")&&recovered.exchanges.Count==2,"refresh recovers lost attending response, role and prompt via idempotent transition");
                session.Draft=new EncounterAssessment{diagnosis="Gastroenteritis",differential=new[]{"Ectopic pregnancy","Ureteric stone"},procedure="Observation and reassessment",urgency="elective"};
                session.SubmitAssessment();var assessment=Drain(session);assessment.Run();
                Check(assessment.exchanges.Count==2&&assessment.exchanges[0].path.EndsWith("/tools/record_assessment",StringComparison.Ordinal)&&assessment.exchanges[1].path.EndsWith("/score",StringComparison.Ordinal),"assessment and score use same service encounter; score fetch follows successful assessment only");
                Check(session.State.phase=="scored"&&session.Score!=null&&session.Score.total<100&&session.State.assessment.diagnosis=="Gastroenteritis","service evaluates learner's actual imperfect assessment; no local score engine");
                Property(session,"Score",null);session.RefreshState();var recoveredScore=Drain(session);recoveredScore.Run();
                Check(session.Score!=null&&recoveredScore.exchanges.Count==2&&session.State.phase=="scored","refresh recovers scored card without repeating assessment mutation");
                session.StartPatient(EncounterContract.MalePatientId);Drain(session).Run();Check(session.State.patientName=="Jonah Okoye"&&session.State.patientId==EncounterContract.MalePatientId,"male30 persona binds its own synthetic case");
                string maleId=session.State.encounterId;
                session.Ask("onset");var stale=Drain(session);Check(stale.Step(),"old-case actual HTTP request begins");
                session.StartPatient(EncounterContract.FemalePatientId);stale.Run();Drain(session).Run();
                Check(session.State.patientId==EncounterContract.FemalePatientId&&session.State.encounterId!=femaleId&&session.State.encounterId!=maleId&&session.State.historyAsked.Length==0,"new-case generation rejects stale pending request response and resets gathered state");
                voice.ConfigureEndpoint(endpoint);
                session.voice=voice;Property(voice,"Status","connecting");int permissionGeneration=Get<int>(voice,"generation");
                Call(session,"OnApplicationPause",true);Call(session,"OnApplicationFocus",false);
                Check(Get<int>(voice,"generation")==permissionGeneration,"permission-dialog focus/pause preserves pending first-use voice permission flow");session.voice=null;
                VoiceConfiguration(voice,session.State.encounterId,session.State.patientId,"patient","female-voice");
                var init=JsonUtility.FromJson<VoiceInit>((string)Call(voice,"BuildInitiation","patient"));
                Check(init.dynamic_variables.encounter_id==session.State.encounterId&&init.dynamic_variables.session_id==session.State.encounterId&&init.dynamic_variables.coach_session_id==""&&init.dynamic_variables.patient_id==EncounterContract.FemalePatientId&&init.dynamic_variables.mode=="patient","serialized patient voice initiation uses exact encounter identity and distinct role");
                Check(init.conversation_config_override.tts.voice_id=="female-voice"&&init.conversation_config_override.agent.prompt.prompt=="authored prompt","patient voice selects returned voice and authored prompt");
                var begin=new Pump((IEnumerator)Call(voice,"BeginConversation",Get<int>(voice,"generation")));begin.Run();
                Check(begin.exchanges.Count==2&&begin.exchanges[0].path=="/encounters/"+session.State.encounterId&&begin.exchanges[1].path=="/jarvis/connection"&&begin.exchanges[1].query=="?agent=patient"&&voice.Status=="error","production voice HTTP validates encounter then requests patient agent; missing credentials fail visibly before WSS");
                session.SeeAttending();Drain(session).Run();VoiceConfiguration(voice,session.State.encounterId,session.State.patientId,"attending","");
                var attendingJson=(string)Call(voice,"BuildInitiation","attending");Check(!attendingJson.Contains("\"tts\"")&&attendingJson.Contains("\"mode\":\"attending\""),"attending keeps Jarvis default voice and separate role");
                var attendBegin=new Pump((IEnumerator)Call(voice,"BeginConversation",Get<int>(voice,"generation")));attendBegin.Run();
                Check(attendBegin.exchanges.Count==2&&attendBegin.exchanges[1].query=="","attending signed connection uses default Jarvis agent");
                VoiceConfiguration(voice,session.State.encounterId,EncounterContract.MalePatientId,"attending","");
                var mismatch=new Pump((IEnumerator)Call(voice,"BeginConversation",Get<int>(voice,"generation")));mismatch.Run();
                Check(mismatch.exchanges.Count==1&&voice.Status=="error","voice patient identity mismatch never reaches provider connection route");
                // Native session tool guard: old provider connection cannot mutate a current encounter.
                session.voice=voice;Set(voice,"generation",90);Property(voice,"Status","connected");connection=NewConnection(voice,90);connections.Add(connection);
                var request=new QuestJarvisVoice.ToolRequest{ToolName="record_assessment",ToolCallId="old-tool",ConnectionGeneration=90,ParametersJson="{\"diagnosis\":\"wrong\"}"};
                Get<HashSet<string>>(voice,"pendingTools").Add(request.ToolCallId);Call(session,"VoiceTool",request);
                voice.Disconnect();var abandoned=Drain(session);abandoned.Run();
                Check(abandoned.exchanges.Count==0&&session.State.phase=="attending","disconnected queued voice call emits no stale HTTP mutation");
                session.voice=null;
            }
            finally
            {
                if(voice)voice.Disconnect();if(fixture)UnityEngine.Object.DestroyImmediate(fixture);
                foreach(var created in connections)foreach(var name in new[]{"Socket","Cancel","SendSignal"})if(created.GetType().GetField(name).GetValue(created) is IDisposable disposable)disposable.Dispose();
                if(server!=null){if(!server.HasExited)server.Kill();server.WaitForExit(5000);server.Dispose();}
            }
        }
        static void Direct(string endpoint,string method,string path,string body)
        {
            using(var request=new UnityWebRequest(endpoint+path,method))
            {
                request.downloadHandler=new DownloadHandlerBuffer();request.timeout=4;
                request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));request.SetRequestHeader("Content-Type","application/json");
                var operation=request.SendWebRequest();var clock=Stopwatch.StartNew();
                while(!operation.isDone){if(clock.ElapsedMilliseconds>6000)throw new InvalidOperationException("Direct fixture HTTP timed out");Thread.Sleep(5);}
                if(request.result!=UnityWebRequest.Result.Success)throw new InvalidOperationException("Direct fixture HTTP status="+request.responseCode);
            }
        }
        static void VoiceConfiguration(QuestJarvisVoice voice,string id,string patient,string role,string voiceId)
        {
            voice.Disconnect();voice.ConfigureEncounterConversation("authored prompt","authored greeting",voiceId,role);Set(voice,"encounterMode",true);Set(voice,"patientId",patient);Property(voice,"CoachSessionId",id);
        }
        static Pump Drain(NativeEncounterSession session)=>new Pump((IEnumerator)Call(session,"Drain"));
        static object NewConnection(QuestJarvisVoice voice,int epoch)
        {
            var type=typeof(QuestJarvisVoice).GetNestedType("Connection",BindingFlags.NonPublic);var connection=Activator.CreateInstance(type,new object[]{epoch});type.GetField("Open").SetValue(connection,true);Set(voice,"active",connection);return connection;
        }
        static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Private).Invoke(target,args);
        static void Set(object target,string name,object value)=>target.GetType().GetField(name,Private).SetValue(target,value);
        static T Get<T>(object target,string name)=>(T)target.GetType().GetField(name,Private).GetValue(target);
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
