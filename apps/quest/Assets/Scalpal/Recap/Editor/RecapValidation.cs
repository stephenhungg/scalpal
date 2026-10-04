using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    public static class RecapValidation
    {
        static int checks;
        static void Check(bool pass,string message) { checks++;if(!pass)throw new InvalidOperationException("Recap validation: "+message); }
        static void Reject(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,message);}
        [MenuItem("Scalpal/Recap/Verify")]
        public static void Run()
        {
            checks=0;
            string json=File.ReadAllText(RecapBuild.Root+"/Fixtures/sample-run-result.json");
            var r=RunResultContract.Parse(json);Check(r.diagnosis.total==82&&r.surgery.total==76,"scores remain independent");
            Check(RunResultContract.Parse(JsonUtility.ToJson(r)).surgery.guardrailViolations[0].atSeconds==48,"roundtrip preserves timestamp");
            Reject(()=>RunResultContract.Parse("{}"),"missing identity rejected");Reject(()=>RunResultContract.Parse("{"),"malformed JSON rejected");
            foreach(var state in new[]{"queued","processing","ready","failed"})
            {
                r.replay.status=state;r.replay.replayVideoUrl=state=="ready"?"https://example.invalid/replay.mp4":"";
                RunResultContract.Validate(r);Check(r.replay.status==state,"valid replay state "+state);
            }
            r.replay.status="surprise";Reject(()=>RunResultContract.Validate(r),"unknown state rejected");r.replay.status="failed";r.replay.failureReason="";Reject(()=>RunResultContract.Validate(r),"failure needs reason");r=RunResultContract.Parse(json);
            Check(RunResultContract.ReplayLabel(r.replay,true).Contains("not your recording"),"fallback visibly labeled");
            r.replay.source="learner";Check(RunResultContract.ReplayLabel(r.replay,false).StartsWith("Your hand motion"),"learner label");
            r.replay.source="rehearsal";Check(RunResultContract.ReplayLabel(r.replay,false).Contains("not this attempt"),"rehearsal label");r.replay.source="learner";
            Check(!RunResultContract.TryClipTime(r,r.surgery.guardrailViolations[0],out _),"unmapped clock cannot seek");r.replay.clockAligned=true;
            Check(RunResultContract.TryClipTime(r,r.surgery.guardrailViolations[0],out var t)&&t==8,"mapped run time seeks clip time");
            Check(!RunResultContract.TryClipTime(r,new TimedFact{atSeconds=900},out _),"out of clip error cannot seek");
            var f=RunResultContract.Feedback(r);Check(f.strengths.Count<=2&&f.improvements.Count<=2,"feedback capped");
            Check(f.strengths[0]=="Elicited: Pain migration"&&f.improvements[0]=="Revisit: Medication history","feedback grounded in exact facts");
            var empty=RunResultContract.Parse(json);empty.diagnosis=null;empty.surgery.available=false;var none=RunResultContract.Feedback(empty);
            Check(none.strengths.Count==0&&none.improvements.Count==0,"missing scores cannot create praise");
            Check(RecapPanel.Clinical(r).Contains("82 / 100")&&RecapPanel.Procedural(r).Contains("76 / 100"),"both displayed scorecards keep separate scores");
            Check(RecapPanel.Procedural(empty).Contains("Not available"),"missing grader not zero");
            var demo=DemoFlags.JudgePath(true);Check(demo.enabled&&demo.showSuggestedQuestions&&demo.skipMarking&&demo.preExpose&&demo.timeLapseNonKeySteps&&demo.replayHighlightSeconds==20,"judge flags complete");
            Check(!DemoFlags.JudgePath(false).skipMarking,"demo off clears bypass flag");
            Check(RecapVideo.PlaybackWindow(60, demo)==20 && RecapVideo.PlaybackWindow(60, DemoFlags.JudgePath(false))==60,"actual video window honors demo flag");
            r.replay.jobId="job-a";var reply=new RecapController.GatewayReplay{schemaVersion="scalpal.replay.v1",sessionId=r.sessionId,attemptId="old",jobId="job-a",status="processing"};
            Check(!RecapController.ApplyGateway(r,reply),"old attempt response rejected");reply.attemptId=r.attemptId;Check(RecapController.ApplyGateway(r,reply)&&r.replay.status=="processing","gateway running status consumed");
            reply.status="ready";reply.replayVideoUrl="https://example.invalid/a.mp4";reply.replayKind="physics";Check(!RecapController.ApplyGateway(r,reply),"nonkinematic output not mislabeled");reply.replayKind="kinematic";Check(RecapController.ApplyGateway(r,reply),"actual ready response accepted");
            var go=new GameObject("Validation run context");var context=go.AddComponent<RecapRunContext>();context.Begin(RunResultContract.Parse(json));
            Check(context.EndSurgery("sample-attempt",r.surgery),"segment end accepted once");Check(!context.EndSurgery("sample-attempt",r.surgery),"duplicate segment end rejected");
            Check(context.result.replay.status=="failed"&&context.result.replay.failureReason.Contains("adapter"),"missing capture adapter honest");
            Check(!context.AttachMotionJob("old","job",0,true),"stale capture rejected");Check(context.AttachMotionJob("sample-attempt","job",40,true),"capture handoff accepted");
            context.Begin(RunResultContract.Parse(json));Check(!context.SegmentClosed,"fresh attempt resets closure");UnityEngine.Object.DestroyImmediate(go);
            var scene=EditorSceneManager.OpenScene(RecapBuild.ScenePath,OpenSceneMode.Single);var c=UnityEngine.Object.FindFirstObjectByType<RecapController>();
            Check(c&&c.panel&&c.replay&&c.voice&&c.previewResult,"scene required bindings");Check(c.replay.sampleClip&&c.replay.sampleClip.length>=19.9,"20 second fallback asset bound");
            Check(c.replay.robotPlayer.targetTexture&&c.replay.sourcePlayer.targetTexture,"both video panels bound");Check(c.panel.leftCard!=c.panel.rightCard,"scores use separate panels");
            Check(c.panel.errors.Length==3&&c.panel.exploreButton&&c.panel.retryButton,"error markers and next actions bound");
            var rig=UnityEngine.Object.FindFirstObjectByType<RecapInput>();Check(rig&&rig.origin&&rig.head&&rig.left&&rig.right,"headset rig bound");
            Check(!c.replay.robotPlayer.playOnAwake && !c.replay.sourcePlayer.playOnAwake,"videos require explicit playback");
            Debug.Log("SCALPAL_RECAP_VERIFY_OK checks="+checks+" headset=false provider=false");
        }
    }
}
