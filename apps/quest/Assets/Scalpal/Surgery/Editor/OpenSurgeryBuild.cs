using System;
using System.Linq;
using System.IO;
using System.Reflection;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using Scalpal.Quest.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    public static class OpenSurgeryBuild
    {
        public static void Verify()
        {
            OpenBodyValidation.Run(); OpenBodyInteractionValidation.Run(); OpenSurgeryCoachValidation.Run(); OpenBodyBloodValidation.Run(); MarkingGuideValidation.Run();
            var volume=TissueVolumeFactory.OpenAbdominalWall();
            if(volume.Materials.Length!=5 || string.Join(",",volume.Materials.Select(m=>m.id))!="skin,fat,fascia,muscle,peritoneum")throw new Exception("Open volume material identities missing");
            AuditScene();
            Debug.Log("SCALPAL_OPEN_SURGERY_VERIFY_OK: synthetic component exchange and actual-scene delivery mechanics; physical headset session pending");
        }
        // Package existing enabled scenes without modifying their owner-authored composition.
        // This component build does not claim that the separate Shell validation gate passed.
        public static void BuildAndroid()
        {
            string output=Environment.GetEnvironmentVariable("SCALPAL_OPEN_APK");
            if(string.IsNullOrWhiteSpace(output)||!Path.IsPathRooted(output))throw new InvalidOperationException("SCALPAL_OPEN_APK must be an absolute path");
            Verify();
            var scenes=EditorBuildSettings.scenes.Where(s=>s.enabled).Select(s=>s.path).ToArray();
            if(!scenes.Contains(NativeSessionBuild.ScenePath))throw new InvalidOperationException("Native OR scene is not enabled for build");
            var preloads=PlayerSettings.GetPreloadedAssets(); var oldBundle=EditorUserBuildSettings.buildAppBundle;
            var oldHttp=PlayerSettings.insecureHttpOption;
            try
            {
                EditorSceneManager.OpenScene(scenes[0],OpenSceneMode.Single);
                EditorUserBuildSettings.buildAppBundle=false;
                PlayerSettings.insecureHttpOption=InsecureHttpOption.DevelopmentOnly;
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                var result=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=scenes,target=BuildTarget.Android,locationPathName=output,options=BuildOptions.Development});
                if(result.summary.result!=BuildResult.Succeeded)throw new InvalidOperationException("Open surgery Android package failed: "+result.summary.result);
                Debug.Log("SCALPAL_OPEN_ANDROID_BUILD_OK bytes="+new FileInfo(output).Length+" scenes="+scenes.Length+" installed=false shellGate=false");
            }
            finally
            {
                PlayerSettings.SetPreloadedAssets(preloads);EditorUserBuildSettings.buildAppBundle=oldBundle;
                PlayerSettings.insecureHttpOption=oldHttp;AssetDatabase.SaveAssets();
            }
        }
        public static void AuditScene()
        {
            if(!Application.isBatchMode)throw new InvalidOperationException("Use batch mode for the disposable scene audit");
            var previous=EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue=null;
            try
            {
                var adapter=ConfigureSceneAttempt(out var session,out tissue);
                var exercise=session.exercise;
                if(session.workbench.tools.Count(t=>t.instrumentId=="retractor")<2 || session.workbench.tools.Count(t=>t.instrumentId=="hemostat")<2)throw new Exception("Paired instruments absent");
                var counts=adapter.Interaction.Targets.Count(t=>t.HasBase);
                foreach(var definition in exercise.Body.Tissues.Where(t=>t.order<0))
                    if(!adapter.Interaction.Targets.Any(t=>t.tissueId==definition.id))throw new Exception("Missing actual anatomy contact target "+definition.id);
                Debug.Log("SCALPAL_OPEN_SCENE_BINDINGS_OK bases="+counts+" tools="+session.workbench.tools.Length+" mobileGroups="+adapter.Interaction.Mobility.Count+" status="+adapter.Status+" source=synthetic_editor nativeLoaderStillOwnsProcedure=true");
            }
            finally
            {
                if(tissue)tissue.Dispose();
                RestoreScenes(previous);
            }
        }
        // Opens the actual native session scene and composes a selected open case exactly as the
        // runtime does, with networking/voice/input disabled. Callers own disposal and scene restore.
        internal static OpenSurgerySession ConfigureSceneAttempt(out NativeCaseSession session,out NativeTissueSimulation tissue)
        {
            session=null; tissue=null;
            var scene=EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath,OpenSceneMode.Single);
            session=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<NativeCaseSession>(true)).Single();
            session.enabled=false; session.realtime.autoConnect=false;session.realtime.enabled=false;session.voice.enabled=false;session.coach.enabled=false;
            session.workbench.enabled=false; foreach(var hand in session.workbench.inputs)hand.enabled=false;
            typeof(NativeWorkbench).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(session.workbench,null);
            var asset=Resources.Load<TextAsset>("scalpal_bundle");var bundle=JsonUtility.FromJson<ScalpalBundle>(asset.text);
            var selected=bundle.cases.First(c=>c.procedureId=="open_appendectomy" || c.procedureId=="lap_appendectomy");
            selected.procedure=bundle.procedures.Single(p=>p.id=="open_appendectomy"); selected.procedureId=selected.procedure.id;
            selected.caseId="synthetic_open_scene_binding";selected.instruments=bundle.instruments;
            var exercise=session.exercise;exercise.anatomy=session.anatomy;exercise.requireCoachSynchronization=false;
            session.anatomy.SetPreviewMode(false);session.anatomy.SetRegistrationValid(true);
            if(!exercise.SelectCase(bundle,selected.caseId,true,out string reason))throw new Exception("Open case scene binding: "+reason);
            tissue=session.GetComponent<NativeTissueSimulation>()??session.gameObject.AddComponent<NativeTissueSimulation>();
            tissue.Initialize(session.anatomy,session.workbench,()=>false);
            var volume=session.GetComponent<NativeVolumeSimulation>()??session.gameObject.AddComponent<NativeVolumeSimulation>();
            var adapter=session.GetComponent<OpenSurgerySession>()??session.gameObject.AddComponent<OpenSurgerySession>();
            typeof(OpenSurgerySession).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
            typeof(OpenSurgerySession).GetMethod("ConfigureAttempt",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
            if(!adapter.Interaction||!adapter.Wound||volume.Wall.Volume.Materials.Length!=5)throw new Exception("Open composition did not bind instrument/body/wall adapters");
            return adapter;
        }
        internal static void RestoreScenes(UnityEditor.SceneManagement.SceneSetup[] previous)
        {
            if(previous.Any(s=>s.isLoaded)&&previous.Any(s=>s.isActive))EditorSceneManager.RestoreSceneManagerSetup(previous);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        }
    }
}
