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
    // Real scene lifecycle/physics with deterministic XR input and isolated real backend routes.
    // This is Editor Play Mode evidence, not a physical headset or complete demo-flow test.
    [InitializeOnLoad]
    public static class NativeSessionPlayModeValidation
    {
        const string Running="Scalpal.NativePlayMode.Running", Passed="Scalpal.NativePlayMode.Passed";
        static NativeSessionPlayModeValidation() => EditorApplication.playModeStateChanged += Changed;
        public static void Run()
        {
            string path=Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG");
            if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new InvalidOperationException("Use scripts/quest/play-mode-check/run.py with the isolated local fixture");
            var config=JsonUtility.FromJson<NativeCaseSession.DevelopmentConfig>(File.ReadAllText(path));
            if(config==null||!config.database.StartsWith("scalpal-test-playmode-")||new Uri(config.coachBaseUrl).Host!="127.0.0.1")
                throw new InvalidOperationException("Play Mode requires a throwaway local configuration");
            EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath,OpenSceneMode.Single);
            SessionState.SetBool(Running,true);SessionState.SetBool(Passed,false);
            EditorApplication.EnterPlaymode();
        }
        static void Changed(PlayModeStateChange state)
        {
            if(!SessionState.GetBool(Running,false))return;
            if(state==PlayModeStateChange.EnteredPlayMode)
                new GameObject("NativeSessionPlayModeDriver").AddComponent<NativeSessionPlayModeDriver>();
            if(state==PlayModeStateChange.EnteredEditMode)
            {
                bool passed=SessionState.GetBool(Passed,false);SessionState.SetBool(Running,false);
                XRInput.RestoreUnitySource();EditorApplication.Exit(passed?0:1);
            }
        }
    }

}
