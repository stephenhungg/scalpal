using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    // Actual Editor Play Mode / decoder smoke test. Never requests microphone or participant capture.
    [InitializeOnLoad]
    public static class RecapPlaybackValidation
    {
        const string Key = "ScalpalRecapPlaybackCheck";
        static RecapPlaybackValidation() { if (SessionState.GetBool(Key, false)) EditorApplication.update += Tick; }
        public static void Run()
        {
            EditorSceneManager.OpenScene(RecapBuild.ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(Key, true); SessionState.SetInt(Key+"Stage",0);
            SessionState.SetString(Key+"Deadline",(EditorApplication.timeSinceStartup+60).ToString(System.Globalization.CultureInfo.InvariantCulture));
            EditorApplication.update -= Tick; EditorApplication.update += Tick;
            EditorApplication.EnterPlaymode();
        }
        static void Tick()
        {
            if (!SessionState.GetBool(Key,false)) return;
            try
            {
                double deadline=double.Parse(SessionState.GetString(Key+"Deadline","0"),System.Globalization.CultureInfo.InvariantCulture);
                if(EditorApplication.timeSinceStartup>deadline)throw new InvalidOperationException("Sample decoder / Play Mode timed out.");
                if(!EditorApplication.isPlaying)return;
                int stage=SessionState.GetInt(Key+"Stage",0);
                if(stage==3)
                {
                    var hub=UnityEngine.Object.FindFirstObjectByType<Scalpal.Shell.HubController>();
                    if(!hub||!hub.Exploring)return;
                    SessionState.SetBool(Key,false);EditorApplication.update-=Tick;
                    Debug.Log("SCALPAL_RECAP_PLAYBACK_OK samplePrepared=true seek=true playPause=true phaseProgression=true dualScores=true exploreNavigation=true headset=false provider=false");
                    EditorApplication.Exit(0);return;
                }
                var c=UnityEngine.Object.FindFirstObjectByType<RecapController>();if(!c||c.Result==null)return;
                var v=c.replay;
                if(stage==0)
                {
                    if(!v.robotPlayer.isPrepared)return;
                    if(!v.Fallback||!c.Result.isSample||Math.Abs(v.Duration-20)>.1)throw new InvalidOperationException("Initial fallback / demo window mismatch.");
                    v.Seek(8);SessionState.SetInt(Key+"Stage",1);
                }
                else if(stage==1)
                {
                    if(Math.Abs(v.Position-8)>.15)return;
                    v.TogglePlay();SessionState.SetInt(Key+"Stage",2);
                }
                else if(stage==2)
                {
                    if(v.Position<8.4)return;
                    if(!v.Playing)throw new InvalidOperationException("Sample did not play.");
                    v.Pause();
                    string dir=Environment.GetEnvironmentVariable("SCALPAL_RECAP_PREVIEW")??"/tmp/scalpal-recap-playback";Directory.CreateDirectory(dir);
                    RecapBuild.Capture(dir+"/replay.png");
                    c.Advance();if(c.Phase!="reaction")throw new InvalidOperationException("Reaction transition failed.");
                    c.Advance();if(c.Phase!="self")throw new InvalidOperationException("Self assessment transition failed.");
                    c.Advance();if(c.Phase!="scores"||!c.panel.leftCard.text.Contains("82 / 100")||!c.panel.rightCard.text.Contains("76 / 100"))throw new InvalidOperationException("Score reveal failed.");
                    RecapBuild.Capture(dir+"/recap.png");
                    c.Navigate(false);SessionState.SetInt(Key+"Stage",3);
                }

            }
            catch(Exception e)
            {
                SessionState.SetBool(Key,false);EditorApplication.update-=Tick;Debug.LogException(e);EditorApplication.Exit(1);
            }
        }
    }
}
