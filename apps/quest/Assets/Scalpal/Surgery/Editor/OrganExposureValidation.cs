using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Real imported geometry, native composition and tool consumer; synthetic tracking and
    // authored prior-wall fixtures. This is exposure/contact evidence, not a headset run.
    public static class OrganExposureValidation
    {
        const float Dt=.02f;
        static int checks;
        static void Require(bool value,string message)
        {checks++;if(!value)throw new InvalidOperationException("Organ exposure: "+message);}
        [MenuItem("Scalpal/Surgery/Validate Appendix Base Exposure")]
        public static void Run()
        {
            checks=0;VerifyAngularMeasurement();
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())throw new InvalidOperationException("Save/discard scene edits before organ exposure validation");
            var previous=EditorSceneManager.GetSceneManagerSetup();NativeTissueSimulation tissue=null;
            try
            {
                var adapter=OpenSurgeryBuild.ConfigureSceneAttempt(out var session,out tissue);
                session.presentation.passthrough=false;session.presentation.Apply();
                var s=new Rig(session,adapter,tissue);
                s.NewAttempt();var vr=Exposure(s,"vr");
                VerifyPauseAndReset(s);
                var registration=session.patientFrame.root;
                registration.SetPositionAndRotation(registration.position+new Vector3(.7f,.13f,-.4f),Quaternion.Euler(7,54,-9)*registration.rotation);
                session.presentation.passthrough=true;session.presentation.SyntheticCompositorReady=true;session.presentation.Apply();Physics.SyncTransforms();
                s.NewAttempt();var ar=Exposure(s,"ar_registered");
                Require(Vector3.Distance(vr,ar)<.006f,"AR transformed registration preserves the exposed4 mm station relative to the body");
                Debug.Log("SCALPAL_ORGAN_EXPOSURE_OK checks="+checks+" actualScene=true actualMeshContact=true wristConsumer=true syntheticPriorWall=true headset=false");
            }
            finally {if(tissue)tissue.Dispose();OpenSurgeryBuild.RestoreScenes(previous);}
        }
        sealed class Rig
        {
            public readonly NativeCaseSession session;public readonly OpenSurgerySession adapter;
            public readonly OpenBodyInteraction input;public readonly NativeTissueSimulation tissue;
            public readonly Transform wound;public readonly AnatomyExerciseBinding exercise;
            public readonly AnatomyPart appendix;public readonly Transform[] parts;
            public readonly Vector3[] restPositions;public readonly Quaternion[] restRotations;
            public readonly List<BodyRecord> records=new List<BodyRecord>();public bool gate=true;
            readonly Vector3 registeredUp;
            public Vector3 Up=>session.patientFrame.root.TransformDirection(registeredUp);
            public OrganMobilization Group=>input.Mobility.Single();
            public SurgeryTissueTarget Target=>input.Targets.Single(t=>t.tissueId=="appendix");
            public Rig(NativeCaseSession owner,OpenSurgerySession open,NativeTissueSimulation native)
            {
                session=owner;adapter=open;tissue=native;input=open.Interaction;wound=open.Wound.transform;exercise=owner.exercise;registeredUp=owner.patientFrame.root.InverseTransformDirection(Vector3.up);
                input.Initialize(exercise,session.workbench.tools,session.patientFrame,wound,()=>gate);
                Require(input.ConfigureMobility(adapter.mobileOrganGroups,out var reason)&&input.Mobility.Count==1,"actual appendix group resolves: "+reason);
                Require(Group.Definition.maxGripRotationDegrees>=79&&Group.Definition.maxGripRotationDegrees<=81,"appendix scene enables the bounded80 degree wrist rotation");
                Require(OpenSurgeryAnatomy.Bind(session.anatomy,input),"source atlas owns the longitudinal base references");
                Require(session.anatomy.TryGetPart("appendix",out appendix),"actual appendix atlas part exists");
                parts=Group.Parts.ToArray();restPositions=parts.Select(p=>p.localPosition).ToArray();restRotations=parts.Select(p=>p.localRotation).ToArray();
                Require(parts.Length==4,"appendix, cecum, mesentery and artery share mobilization");
                tissue.Initialize(session.anatomy,session.workbench,()=>gate);input.Submitted+=(r,_)=>records.Add(r);
            }
            public InstrumentBehaviour Tool(string id)=>session.workbench.tools.First(t=>t&&t.instrumentId==id);
            public void Place(InstrumentBehaviour tool,Vector3 point,Quaternion rotation)
            {tool.transform.rotation=rotation;tool.transform.position+=point-tool.actionPoint.position;Physics.SyncTransforms();}
            public void Step(int count=1)
            {
                for(int i=0;i<count;i++)
                {
                    input.Simulate(Dt);tissue.Simulate(Dt);adapter.Wound.SetRegistrationValid(gate);adapter.Wound.Apply(exercise.Body);
                    session.GetComponent<SurgicalOrganAppearance>()?.Refresh();session.GetComponent<SurgicalActionAppearance>()?.Refresh();session.GetComponent<NativeOperatingRoomLighting>()?.Refresh();Physics.SyncTransforms();
                }
            }
            public void NewAttempt()
            {
                foreach(var tool in session.workbench.tools)if(tool){tool.SetHeld(false);tool.SetActivation(0);tool.GetComponent<SurgeryInstrumentLatch>()?.Clear();}
                var selected=exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle{cases=new[]{selected}},selected.caseId,true,out var why),"fresh exposure attempt: "+why);
                gate=true;session.anatomy.SetRegistrationValid(true);Step();records.Clear();
                // Wall work is a disclosed prior-state fixture, not this validation's claim.
                foreach(var step in selected.procedure.steps.Take(5))foreach(var e in CaseRunner.PerfectEvents(step))Require(exercise.Submit(e,out _,out var error),"authored prior wall fixture: "+error);
                Require(exercise.Body.Get("peritoneum","opened")==1&&exercise.Body.Get("appendix","delivered")==0,"fixture opens wall but does not award delivery/base work");Step();
            }
            public Vector3 GripSite()
            {
                var mesh=appendix.GetComponent<MeshFilter>();var collider=appendix.GetComponent<MeshCollider>();
                Vector3 centre=mesh.transform.TransformPoint(mesh.sharedMesh.bounds.center);
                Require(collider.Raycast(new Ray(centre-wound.forward*.1f,wound.forward),out var hit,.2f),"real imported appendix has a graspable upper surface");return hit.point-wound.forward*.002f;
            }
            public InstrumentBehaviour Deliver()
            {
                var tool=Tool("babcock");Require(tool&&tool.actionPoint,"native babcock has a tracked action point");
                tool.SetHeld(true);tool.SetTrackingValid(true);tool.SetActivation(1);Place(tool,GripSite(),wound.rotation);Step(6);
                Require(Group.Held,"consumer obtains an actual appendix-group grip");
                Vector3 from=wound.InverseTransformPoint(tool.actionPoint.position),to=new Vector3(0,0,-.03f);
                int count=Mathf.CeilToInt(Vector3.Distance(from,to)/.0015f);
                for(int i=1;i<=count;i++){Place(tool,wound.TransformPoint(Vector3.Lerp(from,to,i/(float)count)),wound.rotation);Step();}
                Step(8);Require(exercise.Body.Get("appendix","delivered")==1&&Group.Held,"real grasp translation delivers the organ while keeping the grip");return tool;
            }
        }
        static Vector3 Exposure(Rig s,string label)
        {
            var tool=s.Deliver();var target=s.Target;
            Vector3 fixedGrip=tool.actionPoint.position;Quaternion initialRotation=tool.transform.rotation;
            Require(target.TryContact(fixedGrip,.004f,out var heldSurface),"held tool remains at an actual appendix contact before wrist rotation");

            var close=s.wound.position+s.Up*.22f-s.session.patientFrame.right*.14f-s.session.patientFrame.forward*.05f;
            var organTarget=target.basePoint.position;
            var organEye=organTarget+s.Up*.16f-s.session.patientFrame.right*.09f+s.session.patientFrame.forward*.03f;
            var views=new[]{("close",close,s.wound.position,40f),("organs",organEye,organTarget,34f)};
            var before=Render(s,views,label+"_before");
            var baseParent=target.basePoint.parent;var baseLocal=target.basePoint.localPosition;
            var beforeCenters=Centers(s);var beforeRotations=s.parts.Select(p=>p.rotation).ToArray();
            var oldMilestones=s.exercise.CompletedMilestones?.ToArray()??Array.Empty<string>();
            var keys=new[]{"removed","stumpLengthMm","cutAboveTie","decision_true_base","crushed","tieCount","clampCount"};
            var facts=keys.Select(k=>s.exercise.Body.Get("appendix",k)).ToArray();int firstRecord=s.records.Count;
            var baseline=CoverageAt(s,close,organEye,organTarget);
            Debug.Log("SCALPAL_ORGAN_EXPOSURE_BASELINE "+label+" "+baseline.Description);
            var initialTilt=s.Group.Tilt;float worstAngle=0,worstGripError=0,worstFreeGripError=0;
            Quaternion bestGoal=initialRotation;Vector3 chosenAxis=Vector3.zero;float chosenAngle=0;
            Coverage best=null;
            var axes=new List<Vector3>{s.wound.right,-s.wound.right,s.wound.up,-s.wound.up,s.wound.forward,-s.wound.forward};
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2)
                axes.Add((s.wound.right*x+s.wound.up*y+s.wound.forward*z).normalized);
            for(int candidate=0;candidate<axes.Count;candidate++)foreach(float degrees in candidate<6?new[]{40f,80f}:new[]{80f})
            {
                Vector3 axis=axes[candidate];var goal=Quaternion.AngleAxis(degrees,axis)*initialRotation;
                SettleWrist(s,tool,fixedGrip,initialRotation,initialTilt,goal,ref worstAngle,ref worstGripError,ref worstFreeGripError);
                var current=CoverageAt(s,close,organEye,organTarget);float error=Vector3.Distance(s.Group.GripWorldPosition,tool.actionPoint.position);
                Debug.Log($"SCALPAL_ORGAN_EXPOSURE_CANDIDATE {label} axis={s.wound.InverseTransformDirection(axis)} degrees={degrees} gripMm={error*1000:F3} {current.Description}");
                if(error<.004f&&(best==null||current.Worst>best.Worst+1e-7f||Mathf.Abs(current.Worst-best.Worst)<1e-7f&&current.Average>best.Average+1e-7f))
                {best=current;bestGoal=goal;chosenAxis=axis;chosenAngle=degrees;}
            }
            // Revisit the winner through the same bounded consumer. Never restore a pose
            // by assigning organ transforms or reusing an old witness after motion.
            Require(best!=null,label+": at least one wrist pose preserves the tethered grip");
            SettleWrist(s,tool,fixedGrip,initialRotation,initialTilt,bestGoal,ref worstAngle,ref worstGripError,ref worstFreeGripError);
            var selected=CoverageAt(s,close,organEye,organTarget);
            float selectedGripError=Vector3.Distance(s.Group.GripWorldPosition,tool.actionPoint.position);
            Debug.Log($"SCALPAL_ORGAN_EXPOSURE_BEST {label} axis={s.wound.InverseTransformDirection(chosenAxis)} degrees={chosenAngle} gripMm={selectedGripError*1000:F3} {selected.Description}");
            Require(selectedGripError<.004f&&selected.Worst>=.25f,label+": best wrist pose exposes at least25% of both3 mm and4 mm actual sections from BOTH fixed patient-right views; "+selected.Description);
            Require(selected.HasWitness[2]&&selected.HasWitness[0],"visible actual4 mm clamp and3 mm tie stations remain available in the selected pose");
            Vector3 found=selected.Probes[2],hitPoint=selected.Hits[2];
            Require(baseParent && baseParent.IsChildOf(s.appendix.transform) && target.basePoint.parent==baseParent
                && target.basePoint.localPosition==baseLocal && Vector3.Distance(baseParent.TransformPoint(baseLocal),target.basePoint.position)<.000001f,
                "base reference retains its source attachment and rotates with that source pose, including a stationary held-point pivot");
            for(int p=0;p<s.parts.Length;p++)Require(MeasuredAngle(beforeRotations[p],s.parts[p].rotation)>5,"rotation affects every group member including its contact transform");
            Require(Centers(s).Zip(beforeCenters,Vector3.Distance).Max()>.004f&&selectedGripError<.004f,"real mesh centres rotate while the chosen exposure retains its material grip within4 mm of the tool tip");
            float mm=float.NaN;
            Require(target.TryContact(found,.003f,out var contact)&&target.DistanceFromBase(found,out mm)&&Mathf.Abs(mm-4)<.02f&&Vector3.Distance(contact,hitPoint)<.003f,"station remains actual imported contact at4 mm after rotation");
            Require((s.exercise.CompletedMilestones??Array.Empty<string>()).SequenceEqual(oldMilestones)&&keys.Select(k=>s.exercise.Body.Get("appendix",k)).SequenceEqual(facts),"wrist exposure alone earns no base procedure progress");
            Require(s.records.Skip(firstRecord).All(r=>r.action.verb=="grasp"||r.action.verb=="tick"),"rotation produces only measured grip/time records, no clamp/tie/cut/decision action");
            var after=Render(s,views,label+"_after");if(before!=null&&after!=null)for(int i=0;i<before.Length;i++)Require(Changed(before[i],after[i])>150,"fixed patient-right "+views[i].Item1+" view shows an actual organ-pose change");
            // A real different tool must acquire that target and measure that station.
            var clamp=s.Tool("right_angle_clamp");int count=s.records.Count;clamp.SetHeld(true);clamp.SetTrackingValid(true);clamp.SetActivation(1);
            s.Place(clamp,found,s.wound.rotation);s.Step();
            Require(s.records.Skip(count).Any(r=>r.action.verb=="clamp"&&r.action.tissueId=="appendix"&&Math.Abs(r.action.distanceMm-4)<.55&&r.action.choice!="longitudinal_unmeasured"),"actual consumer routes a clamp to the exposed appendix4 mm station");
            clamp.SetActivation(0);clamp.SetHeld(false);clamp.GetComponent<SurgeryInstrumentLatch>()?.Clear();
            var tie=s.Tool("suture_tie");count=s.records.Count;tie.SetHeld(true);tie.SetTrackingValid(true);tie.SetActivation(1);
            s.Place(tie,selected.Probes[0],s.wound.rotation);s.Step();
            Require(s.records.Skip(count).Any(r=>r.action.verb=="tie"&&r.action.tissueId=="appendix"&&Math.Abs(r.action.distanceMm-3)<.55&&r.action.choice!="longitudinal_unmeasured"),"actual consumer routes a tie to the exposed appendix3 mm station");
            tie.SetActivation(0);tie.SetHeld(false);
            Debug.Log($"SCALPAL_ORGAN_EXPOSURE_DETAIL {label} baselineWorst={baseline.Worst:F3} selectedWorst={selected.Worst:F3} axis={s.wound.InverseTransformDirection(chosenAxis)} degrees={chosenAngle} angularStep={worstAngle:F3} stationMm={mm:F3} selectedGripMm={selectedGripError*1000:F3} worstFreeGripMm={worstFreeGripError*1000:F3} worstTetherGripMm={worstGripError*1000:F3} graphics={before!=null}");
            return s.wound.InverseTransformPoint(hitPoint);
        }
        static Vector3[] Centers(Rig s)=>s.parts.Select(p=>p.TransformPoint(p.GetComponent<MeshFilter>().sharedMesh.bounds.center)).ToArray();
        static Vector3[] RigidCenters(Rig s)=>s.parts.Select(p=>
        {
            var filter=p.GetComponent<MeshFilter>();var tissue=p.GetComponent<DeformableTissue>();
            var mesh=tissue&&tissue.SourceMesh?tissue.SourceMesh:filter.sharedMesh;
            return filter.transform.TransformPoint(mesh.bounds.center);
        }).ToArray();
        static void SettleWrist(Rig s,InstrumentBehaviour tool,Vector3 fixedGrip,Quaternion initialTool,Quaternion initialTilt,Quaternion goal,
            ref float worstAngle,ref float worstGripError,ref float worstFreeGripError)
        {
            Quaternion reference=s.session.anatomy.transform.rotation;
            var delta=Quaternion.Inverse(reference)*(goal*Quaternion.Inverse(initialTool))*reference;
            var desired=Quaternion.RotateTowards(Quaternion.identity,delta*initialTilt,s.Group.Definition.maxGripRotationDegrees);
            int stable=0;
            for(int i=0;i<400;i++)
            {
                var previous=s.Group.Tilt;var last=s.parts.Select(p=>p.rotation).ToArray();var lastCenters=RigidCenters(s);
                var pivot=lastCenters.Aggregate(Vector3.zero,(sum,p)=>sum+p)/lastCenters.Length;
                s.Place(tool,fixedGrip,goal);s.Step();float angle=(float)MeasuredAngle(previous,s.Group.Tilt);worstAngle=Mathf.Max(worstAngle,angle);
                bool withinRate=MeasuredAngle(previous,s.Group.Tilt)<=s.Group.Definition.maxTiltDegreesPerSecond*Dt+.025f;
                Require(withinRate,withinRate?"consumer wrist rotation respects the angular speed limit":"consumer wrist rotation exceeds the angular speed limit; "+AngleDiagnostic(previous,s.Group.Tilt,s.Group.Definition.maxTiltDegreesPerSecond*Dt,i));
                var centres=RigidCenters(s);
                for(int p=0;p<s.parts.Length;p++)
                {
                    bool memberWithinRate=MeasuredAngle(last[p],s.parts[p].rotation)<=s.Group.Definition.maxTiltDegreesPerSecond*Dt+.025f;
                    Require(memberWithinRate,memberWithinRate?"every member transform shares the same bounded wrist motion":"member angular limit exceeded; member="+s.parts[p].name+" "+AngleDiagnostic(last[p],s.parts[p].rotation,s.Group.Definition.maxTiltDegreesPerSecond*Dt,i));
                    float bound=s.Group.Definition.maxSpeedMps*Dt+angle*Mathf.Deg2Rad*Vector3.Distance(lastCenters[p],pivot)+.0001f;
                    Require(Vector3.Distance(centres[p],lastCenters[p])<=bound,"source contact-frame centre travel obeys the linear and angular motion bounds");
                }
                Require(MeasuredAngle(Quaternion.identity,s.Group.Tilt)<=s.Group.Definition.maxGripRotationDegrees+.025f,"actual organ rotation respects its total authored-pose bound");
                float gripError=Vector3.Distance(s.Group.GripWorldPosition,tool.actionPoint.position);worstGripError=Mathf.Max(worstGripError,gripError);
                if(s.Group.OffsetMeters.magnitude<s.Group.Definition.maxTravelMm*.001f-.0001f)
                {worstFreeGripError=Mathf.Max(worstFreeGripError,gripError);Require(gripError<.0001f,"captured material grip stays at the tool tip when not constrained by the tether");}
                Require(s.Group.Held&&s.Group.OffsetMeters.magnitude<=s.Group.Definition.maxTravelMm*.001f+.00002f,"rotation retains its grip and the existing tether bound");
                stable=MeasuredAngle(desired,s.Group.Tilt)<.06f&&angle<.03f?stable+1:0;
                if(stable>=4)return;
            }
            throw new InvalidOperationException("Wrist pose did not converge within400 bounded frames; remainingDegrees="+MeasuredAngle(desired,s.Group.Tilt));
        }
        // Float Quaternion.Angle assumes unit input and evaluates acos near1. The
        // squared-norm drift from repeated Slerp/transform products can inflate a0.9°
        // step. Normalize in double and use quaternion chord/sum atan2, whose small
        // angle is represented by the chord rather than subtraction of float dot from1.
        static double MeasuredAngle(Quaternion a,Quaternion b)
        {
            double an=QuaternionNorm(a),bn=QuaternionNorm(b);
            if(!(an>1e-12)||!(bn>1e-12)||double.IsNaN(an)||double.IsNaN(bn)||double.IsInfinity(an)||double.IsInfinity(bn))return double.PositiveInfinity;
            double ax=a.x/an,ay=a.y/an,az=a.z/an,aw=a.w/an,bx=b.x/bn,by=b.y/bn,bz=b.z/bn,bw=b.w/bn;
            if(ax*bx+ay*by+az*bz+aw*bw<0){bx=-bx;by=-by;bz=-bz;bw=-bw;}
            double dx=ax-bx,dy=ay-by,dz=az-bz,dw=aw-bw,sx=ax+bx,sy=ay+by,sz=az+bz,sw=aw+bw;
            return 4*Math.Atan2(Math.Sqrt(dx*dx+dy*dy+dz*dz+dw*dw),Math.Sqrt(sx*sx+sy*sy+sz*sz+sw*sw))*180/Math.PI;
        }
        static double QuaternionNorm(Quaternion q)=>Math.Sqrt((double)q.x*q.x+(double)q.y*q.y+(double)q.z*q.z+(double)q.w*q.w);
        static string AngleDiagnostic(Quaternion before,Quaternion after,float rateStep,int frame)=>
            $"frame={frame} normalizedDoubleDeg={MeasuredAngle(before,after):F9} unityFloatDeg={Quaternion.Angle(before,after):F9} productionMaxStepDeg={rateStep:F9} existingRoundoffAllowanceDeg=0.025000000 beforeNorm={QuaternionNorm(before):F12} afterNorm={QuaternionNorm(after):F12} before=({before.x:R},{before.y:R},{before.z:R},{before.w:R}) after=({after.x:R},{after.y:R},{after.z:R},{after.w:R})";
        static void VerifyAngularMeasurement()
        {
            var q=Quaternion.AngleAxis(.9f,new Vector3(1,2,3).normalized);
            Require(Math.Abs(MeasuredAngle(Quaternion.identity,q)-.9)<.00003,"double angular oracle resolves the actual0.9 degree production step");
            var scaled=new Quaternion(q.x*1.0002f,q.y*1.0002f,q.z*1.0002f,q.w*1.0002f);
            Require(Math.Abs(MeasuredAngle(Quaternion.identity,scaled)-.9)<.00003,"angular oracle ignores quaternion norm drift without ignoring physical rotation");
            Require(MeasuredAngle(Quaternion.identity,Quaternion.AngleAxis(.95f,Vector3.right))>.925,"angular oracle still rejects a real0.95 degree step against the unchanged0.925 bound");
            Require(MeasuredAngle(q,new Quaternion(-q.x,-q.y,-q.z,-q.w))<1e-9,"quaternion hemisphere sign does not invent rotation");
            Require(double.IsInfinity(MeasuredAngle(q,new Quaternion(0,0,0,0))),"invalid zero quaternion fails angular validation");
        }
        sealed class Coverage
        {
            public readonly int[] Total=new int[4],Visible=new int[4];
            public readonly Vector3[] Probes=new Vector3[4],Hits=new Vector3[4];public readonly bool[] HasWitness=new bool[4];
            public float Worst=>Enumerable.Range(0,4).Min(i=>Total[i]>0?Visible[i]/(float)Total[i]:0);
            public float Average=>Enumerable.Range(0,4).Average(i=>Total[i]>0?Visible[i]/(float)Total[i]:0);
            public string Description=>string.Join(" ",Enumerable.Range(0,4).Select(i=>(i<2?"3mm":"4mm")+(i%2==0?"_close":"_organs")+"="+Visible[i]+"/"+Total[i]));
        }
        static Coverage CoverageAt(Rig s,Vector3 close,Vector3 organEye,Vector3 organTarget)
        {
            var result=new Coverage();var targets=s.input.Targets.Where(t=>t).ToArray();
            var colliders=targets.SelectMany(t=>t.GetComponentsInChildren<Collider>(true)).Where(c=>c&&c.enabled&&c.gameObject.activeInHierarchy).Distinct().ToArray();
            bool oldBackfaces=Physics.queriesHitBackfaces;Physics.queriesHitBackfaces=true;
            try
            {
                for(int station=0;station<2;station++)
                {
                    float mm=station==0?3:4;var samples=SectionSamples(s,mm);
                    Require(samples.Count>=8,"actual"+mm+" mm section has at least8 unique surface samples, not an isolated lucky vertex");
                    for(int view=0;view<2;view++)
                    {
                        int at=station*2+view;result.Total[at]=samples.Count;Vector3 eye=view==0?close:organEye;
                        foreach(var surface in samples)
                        {
                            if(!VisibleSample(s,surface,mm,eye,view==0?s.wound.position:organTarget,view==0?40:34,targets,colliders,out var probe,out var shown))continue;
                            result.Visible[at]++;
                            if(!result.HasWitness[at]){result.HasWitness[at]=true;result.Probes[at]=probe;result.Hits[at]=shown;}
                        }
                    }
                }
            }
            finally {Physics.queriesHitBackfaces=oldBackfaces;}
            return result;
        }
        static List<Vector3> SectionSamples(Rig s,float mm)
        {
            var target=s.Target;var filter=target.GetComponent<MeshFilter>();var mesh=filter.sharedMesh;
            Vector3 axis=target.LongitudinalWorld,origin=target.basePoint.position+axis*(mm*.001f);
            var result=new List<Vector3>();if(!target.DistanceFromBase(origin,out float initial))return result;origin+=axis*((mm-initial)*.001f);
            var raw=mesh.vertices;var triangles=mesh.triangles;
            void Add(Vector3 point){if(!result.Any(p=>(p-point).sqrMagnitude<1e-10f))result.Add(point);}
            for(int t=0;t<triangles.Length;t+=3)
            {
                var v=new[]{filter.transform.TransformPoint(raw[triangles[t]]),filter.transform.TransformPoint(raw[triangles[t+1]]),filter.transform.TransformPoint(raw[triangles[t+2]])};
                var crosses=new List<Vector3>(3);
                for(int e=0;e<3;e++)
                {
                    var a=v[e];var b=v[(e+1)%3];float da=Vector3.Dot(a-origin,axis),db=Vector3.Dot(b-origin,axis);
                    if(Mathf.Abs(da)<1e-7f)crosses.Add(a);
                    if(da*db<0)crosses.Add(Vector3.LerpUnclamped(a,b,da/(da-db)));
                }
                foreach(var point in crosses)Add(point);
                if(crosses.Count>=2)for(int i=0;i<crosses.Count;i++)for(int j=i+1;j<crosses.Count;j++)Add((crosses[i]+crosses[j])*.5f);
            }
            return result;
        }
        static bool VisibleSample(Rig s,Vector3 surface,float mm,Vector3 eye,Vector3 viewTarget,float fov,SurgeryTissueTarget[] targets,Collider[] colliders,out Vector3 probe,out Vector3 shown)
        {
            probe=shown=default;var target=s.Target;Vector3 axis=target.LongitudinalWorld,toward=eye-surface;
            Vector3 forward=(viewTarget-eye).normalized,right=Vector3.Cross(s.Up,forward).normalized,up=Vector3.Cross(forward,right);
            Vector3 view=surface-eye;float depth=Vector3.Dot(view,forward),halfHeight=depth*Mathf.Tan(fov*.5f*Mathf.Deg2Rad);
            if(depth<.01f||Mathf.Abs(Vector3.Dot(view,up))>halfHeight||Mathf.Abs(Vector3.Dot(view,right))>halfHeight*4/3)return false;
            Vector3 radial=Vector3.ProjectOnPlane(toward,axis).normalized;var point=surface+radial*.0003f;
            string toolId=mm==3?"suture_tie":"right_angle_clamp";
            float radius=Mathf.Min(s.Tool(toolId).contactRadius,.006f);
            if(!target.TryContact(point,radius,out var hit)||!target.DistanceFromBase(point,out float along)||Mathf.Abs(along-mm)>.02f)return false;
            float own=(point-hit).sqrMagnitude;
            if(targets.Any(other=>other!=target&&other.TryContact(point,radius,out var otherHit)&&(point-otherHit).sqrMagnitude<=own))return false;
            var local=s.wound.InverseTransformPoint(point);
            if(Mathf.Abs(local.x)<.065f&&Mathf.Abs(local.y)<.04f&&local.z>-.006f)return false;
            float distance=toward.magnitude;if(distance<.002f)return false;var ray=new Ray(eye,-toward/distance);RaycastHit first=default;bool found=false;
            foreach(var shape in colliders)if(shape.Raycast(ray,out var candidate,distance+.002f)&&(!found||candidate.distance<first.distance)){first=candidate;found=true;}
            if(!found||first.collider.GetComponentInParent<SurgeryTissueTarget>()!=target||Vector3.Distance(first.point,surface)>.001f)return false;
            if(!target.DistanceFromBase(first.point,out float visibleMm)||Mathf.Abs(visibleMm-mm)>1.05f)return false;
            probe=point;shown=first.point;return true;
        }
        static void VerifyPauseAndReset(Rig s)
        {
            var tool=s.Tool("babcock");var positions=s.parts.Select(p=>p.position).ToArray();var rotations=s.parts.Select(p=>p.rotation).ToArray();
            s.gate=false;for(int i=0;i<10;i++){s.Place(tool,tool.actionPoint.position,Quaternion.Euler(90,30,40)*tool.transform.rotation);s.Step();}
            Require(s.parts.Select(p=>p.position).SequenceEqual(positions)&&s.parts.Select(p=>p.rotation).SequenceEqual(rotations)&&!s.Group.Held,"registration pause releases wrist hold and freezes the organ/contact pose");
            s.NewAttempt();Require(s.Group.AtRest&&!s.Group.Held,"retry clears wrist delta and held state");
            for(int p=0;p<s.parts.Length;p++)Require(s.parts[p].localPosition==s.restPositions[p]&&s.parts[p].localRotation==s.restRotations[p],"retry restores the exact authored member pose");
        }
        static Color32[][] Render(Rig s,(string name,Vector3 eye,Vector3 target,float fov)[] views,string label)
        {
            if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)return null;
            string folder=Environment.GetEnvironmentVariable("SCALPAL_ORGAN_EXPOSURE_RENDERS");if(!string.IsNullOrEmpty(folder))Directory.CreateDirectory(folder);
            var hidden=s.session.workbench.tools.Where(t=>t).SelectMany(t=>t.GetComponentsInChildren<Renderer>(true)).Where(r=>!r.forceRenderingOff).ToArray();foreach(var r in hidden)r.forceRenderingOff=true;
            var result=new Color32[views.Length][];
            try
            {
                for(int i=0;i<views.Length;i++)
                {
                    var go=new GameObject("ExposureValidationCamera");var camera=go.AddComponent<Camera>();camera.transform.position=views[i].eye;camera.transform.LookAt(views[i].target,s.Up);camera.fieldOfView=views[i].fov;camera.nearClipPlane=.01f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                    var target=new RenderTexture(960,720,24){antiAliasing=4};camera.targetTexture=target;var old=RenderTexture.active;Texture2D image=null;
                    try {camera.Render();RenderTexture.active=target;image=new Texture2D(960,720,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,960,720),0,0);image.Apply();result[i]=image.GetPixels32();if(!string.IsNullOrEmpty(folder))File.WriteAllBytes(Path.Combine(folder,label+"_"+views[i].name+".png"),image.EncodeToPNG());}
                    finally {RenderTexture.active=old;camera.targetTexture=null;if(image)UnityEngine.Object.DestroyImmediate(image);target.Release();UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(go);}
                }
            }
            finally {foreach(var r in hidden)if(r)r.forceRenderingOff=false;}
            return result;
        }
        static int Changed(Color32[] a,Color32[] b)=>a.Zip(b,(x,y)=>Mathf.Max(Mathf.Abs(x.r-y.r),Mathf.Abs(x.g-y.g),Mathf.Abs(x.b-y.b))>24?1:0).Sum();
    }
}
