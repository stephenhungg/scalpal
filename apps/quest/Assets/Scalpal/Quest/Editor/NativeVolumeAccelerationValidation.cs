using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Same production volume and input with acceleration enabled/disabled. No alternate
    // tissue model or controller-motion oracle; acceptance/topology/forces are compared.
    // Only the ordered noninversion sweeps run in Burst; material energy/history stay managed.
    public static class NativeVolumeAccelerationValidation
    {
        static int checks;
        static void Require(bool ok,string message)
        {checks++;if(!ok)throw new InvalidOperationException("Volume acceleration: "+message);}
        [MenuItem("Scalpal/Quest/Validate Volume Acceleration")]
        public static void Run()
        {
            checks=0;
            ValidateGeometryCache();
            Require(Unity.Burst.BurstCompiler.IsEnabled,"Burst compilation must be enabled for the accelerated comparison");
            var reference=TissueVolumeFactory.OpenAbdominalWall();reference.UseBurstJacobianSolver=false;
            var accelerated=TissueVolumeFactory.OpenAbdominalWall();
            Require(accelerated.UseBurstJacobianSolver,"open wall enables noninversion acceleration");
            Require(!TissueVolumeFactory.AbdominalWall().UseBurstJacobianSolver,"legacy coupon remains unchanged");
            // Coincident lower IDs must refuse without stealing an already live patch.
            foreach(var ids in new[]{new[]{1,2},new[]{2,1}})
            {
                var coincident=TissueVolumeFactory.OpenAbdominalWall();var point=new Vector3(0,0,0);
                Require(coincident.BeginMaterialHandle(ids[0],"skin",point,.002f),"coincident fixture first grip");
                Require(!coincident.BeginMaterialHandle(ids[1],"skin",point,.002f)&&coincident.MaterialHandleCount==1,"coincident second grip refuses atomically for either ID order");
                Require(coincident.SetMaterialHandleTarget(ids[0],point-Vector3.forward*.005f),"original grip retains its target route");
                for(int step=0;step<12;step++)coincident.Step(1f/90,Vector3.zero,Vector3.zero);
                Require(coincident.TryMaterialHandlePosition(ids[0],out var measured)&&measured.z<-.0005f,"failed acquisition preserves the original physical patch, not just its token");
            }
            var managedTimes=new List<double>();var burstTimes=new List<double>();
            foreach(var volume in new[]{reference,accelerated})
            {
                Require(volume.BeginMaterialHandle(1,"skin",new Vector3(-.015f,-.004f,0),.003f),"first material grip acquires");
                Require(volume.BeginMaterialHandle(2,"skin",new Vector3(.015f,.004f,0),.003f),"second material grip acquires");
            }
            for(int i=0;i<35;i++)
            {
                if(i==10)accelerated.ReleaseMaterialHandle(999); // An unknown token cannot rebase live patches.
                if(i==12)
                {
                    Require(accelerated.TryMaterialContact("peritoneum",new Vector3(0,0,.028f),.01f,out var contact),"unrelated boundary material contact fixture");
                    Require(accelerated.BeginMaterialHandle(30,"peritoneum",contact,.002f),"unrelated material can acquire independently");
                    accelerated.ReleaseMaterialHandle(30); // Skin continuation must match the untouched reference.
                }
                if(i==15)
                {
                    var a=new Vector3(-.03f,0,-.001f);var b=new Vector3(.03f,0,-.001f);var c=new Vector3(.03f,0,.003f);
                    int before=reference.CutFaceCount;
                    int r=reference.FractureMaterialSweep("skin",a,b,c,.0002f);
                    int f=accelerated.FractureMaterialSweep("skin",a,b,c,.0002f);
                    Require(r>0&&r==f&&reference.CutFaceCount>before,"same physical fracture rebuilds both binding caches");
                }
                foreach(var volume in new[]{reference,accelerated})
                {
                    volume.SetMaterialHandleTarget(1,new Vector3(-.015f,-.004f,-Mathf.Min(i,10)*.0005f));
                    volume.SetMaterialHandleTarget(2,new Vector3(.015f,.004f,-Mathf.Min(i,10)*.0003f));
                }
                managedTimes.Add(Step(reference));burstTimes.Add(Step(accelerated));
                Require(accelerated.LastStepUsedBurst&&!reference.LastStepUsedBurst,"ordered noninversion Burst path actually executes");
                Compare(reference,accelerated,"cut/grip step "+i);
            }
            reference.Reset();accelerated.Reset();Compare(reference,accelerated,"reset");
            var material=new VolumeMaterial{id="synthetic_spectrum",youngPascals=12000,poissonRatio=.3f,densityKgPerCubicMeter=1000,
                relaxationFractions=new[]{.4f},relaxationSeconds=new[]{.1f},measurementSource="synthetic regression"};
            var elasticMaterial=material;elasticMaterial.id="synthetic_elastic";elasticMaterial.relaxationFractions=null;elasticMaterial.relaxationSeconds=null;
            var er=TissueVolumeFactory.Box(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.01f)),4,4,1,elasticMaterial);
            var eb=TissueVolumeFactory.Box(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.01f)),4,4,1,elasticMaterial);eb.UseBurstJacobianSolver=true;
            Require(er.BeginHandle(new Vector3(0,0,-.005f),.01f)&&eb.BeginHandle(new Vector3(0,0,-.005f),.01f),"analytical-scale elastic coupon grips");
            for(int i=0;i<20;i++)
            {var target=new Vector3(0,0,-.005f-Mathf.Min(i,10)*.0001f);er.Step(1f/90,target,Vector3.zero);eb.Step(1f/90,target,Vector3.zero);
                Require(eb.LastStepUsedBurst,"elastic coupon uses noninversion Burst sweeps");Compare(er,eb,"elastic coupon step "+i);}
            var m=TissueVolumeFactory.Box(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.01f)),2,2,1,material);
            var bVolume=TissueVolumeFactory.Box(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.01f)),2,2,1,material);bVolume.UseBurstJacobianSolver=true;
            for(int i=0;i<20;i++)
            {m.Step(1f/90,Vector3.zero,new Vector3(0,0,-.5f));bVolume.Step(1f/90,Vector3.zero,new Vector3(0,0,-.5f));
                Require(!bVolume.LastStepUsedBurst,"ungrasped Maxwell case requires no noninversion acceleration");Compare(m,bVolume,"Maxwell step "+i);}
            Debug.Log($"SCALPAL_VOLUME_ACCELERATION_VALIDATION_OK checks={checks} managedStepP95Ms={P95(managedTimes):F3} burstStepP95Ms={P95(burstTimes):F3} "
                +"sameInputs=true positionsToleranceMeters=0.00005 couponForceAbsoluteToleranceNewton=0.001 openWallForceEquivalence=true forceRelativeTolerance=0.005 editor=true headset=false");
        }
        [MenuItem("Scalpal/Quest/Validate Accepted Geometry Cache")]
        public static void RunGeometryCache()
        {
            checks=0;ValidateGeometryCache();
            Debug.Log("SCALPAL_GEOMETRY_CACHE_VALIDATION_OK checks="+checks+" exactFloatBits=true sameProductionEquations=true editor=true headset=false");
        }
        static readonly PropertyInfo CacheSwitch=typeof(TissueVolume).GetProperty("UseValidatedGeometryCache",BindingFlags.Instance|BindingFlags.NonPublic);
        static readonly FieldInfo CacheValid=typeof(TissueVolume).GetField("validatedGeometry",BindingFlags.Instance|BindingFlags.NonPublic);
        static readonly FieldInfo Histories=typeof(TissueVolume).GetField("histories",BindingFlags.Instance|BindingFlags.NonPublic);
        static readonly FieldInfo PreviousStrain=typeof(MaxwellHistory).GetField("previous",BindingFlags.Instance|BindingFlags.NonPublic);
        static readonly FieldInfo ViscousStrain=typeof(MaxwellHistory).GetField("viscous",BindingFlags.Instance|BindingFlags.NonPublic);
        static void DisableCache(TissueVolume volume)
        {Require(CacheSwitch!=null&&CacheValid!=null,"internal production cache comparison seam exists");CacheSwitch.SetValue(volume,false);}
        static void ValidateGeometryCache()
        {
            foreach(bool memory in new[]{false,true})
            {
                var material=new VolumeMaterial{id="synthetic_geometry_cache",youngPascals=12000,poissonRatio=.3f,densityKgPerCubicMeter=1000,
                    relaxationFractions=memory?new[]{.4f,.2f}:null,relaxationSeconds=memory?new[]{.1f,.5f}:null,measurementSource="synthetic cache regression"};
                var uncached=TissueVolumeFactory.Box(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.01f)),2,2,1,material);
                var cached=TissueVolumeFactory.Box(new Bounds(Vector3.zero,new Vector3(.04f,.04f,.01f)),2,2,1,material);
                DisableCache(uncached);Require((bool)CacheSwitch.GetValue(cached),"cache is enabled by default");
                bool nonzeroForce=false;
                for(int step=0;step<18;step++)
                {
                    float top=step==12?-.03f:.005f+Mathf.Min(step,8)*.00025f;
                    foreach(var volume in new[]{uncached,cached})
                    {
                        for(int node=0;node<volume.Original.Length;node++)
                        {
                            var rest=volume.Original[node];
                            if(rest.z>0&&(Mathf.Abs(rest.x)>.019f||Mathf.Abs(rest.y)>.019f))
                                Require(volume.SetBoundaryTarget(node,new Vector3(rest.x,rest.y,top)),"actual prescribed loading target");
                        }
                    }
                    if(step==10)
                    {
                        var a=new Vector3(-.03f,0,-.03f);var b=new Vector3(.03f,0,-.03f);var c=new Vector3(.03f,0,.03f);
                        int r=uncached.FractureMaterialSweep(material.id,a,b,c,.001f),f=cached.FractureMaterialSweep(material.id,a,b,c,.001f);
                        Require(r>0&&r==f,"both cache paths publish the same actual coupon cut");
                        CompareExact(uncached,cached,"post-cut committed force refresh");
                    }
                    float dt=step%2==0?1f/90:1f/120;
                    uncached.Step(dt,Vector3.zero,Vector3.zero);cached.Step(dt,Vector3.zero,Vector3.zero);
                    if(step==12)Require(!cached.LastStepAccepted&&!uncached.LastStepAccepted,"inverted prescribed boundary produces an actual rejected trial");
                    else Require(cached.LastStepAccepted,"positive prescribed loading/recovery accepts an actual trial");
                    CompareExact(uncached,cached,(memory?"Maxwell":"elastic")+" geometry cache step "+step);
                    var forces=new Vector3[cached.NodeCount];cached.MeasureNodalForces(forces);
                    foreach(var force in forces)nonzeroForce|=force.sqrMagnitude>1e-10f;
                }
                Require(nonzeroForce,"loaded cache comparison measures nonzero physical internal forces");
                uncached.Reset();cached.Reset();CompareExact(uncached,cached,"cache coupon reset");
                for(int i=0;i<4;i++)
                {uncached.Step(1f/90,Vector3.zero,new Vector3(0,0,-.05f));cached.Step(1f/90,Vector3.zero,new Vector3(0,0,-.05f));CompareExact(uncached,cached,"cache after reset "+i);}
            }
            // Real open-wall topology exercises the cache after joint split/separation,
            // while a held membrane survives an unrelated actual skin cut.
            var wallReference=TissueVolumeFactory.OpenAbdominalWall();var wallCached=TissueVolumeFactory.OpenAbdominalWall();DisableCache(wallReference);
            foreach(var wall in new[]{wallReference,wallCached})
            {
                Require(wall.SplitMaterialSweep("muscle","peritoneum",new Vector3(-.04f,0,.018f),new Vector3(.04f,0,.018f),new Vector3(.04f,0,.028f),.0025f,.035f)>0,
                    "cache wall fixture publishes joint split/interface separation");
                Require(wall.BeginMaterialHandle(1,"peritoneum",new Vector3(0,.003f,.027f),.0005f,.012f),"cache wall fixture grips anterior membrane");
                Require(wall.SetMaterialHandleTarget(1,new Vector3(0,.003f,.022f)),"cache wall fixture requests actual lift");
            }
            CompareExact(wallReference,wallCached,"cache split/interface publication");
            for(int step=0;step<18;step++)
            {
                if(step==8)
                {
                    var a=new Vector3(-.04f,0,-.001f);var b=new Vector3(.04f,0,-.001f);var c=new Vector3(.04f,0,.004f);
                    int r=wallReference.FractureMaterialSweep("skin",a,b,c,.0025f),f=wallCached.FractureMaterialSweep("skin",a,b,c,.0025f);
                    Require(r>0&&r==f,"cache wall fixture cuts skin without releasing held membrane");
                    CompareExact(wallReference,wallCached,"wall cut committed force refresh");
                }
                wallReference.Step(1f/90,Vector3.zero,Vector3.zero);wallCached.Step(1f/90,Vector3.zero,Vector3.zero);
                CompareExact(wallReference,wallCached,"cache held membrane step "+step);
            }
            wallReference.Reset();wallCached.Reset();CompareExact(wallReference,wallCached,"cache open-wall reset");
            Debug.Log("SCALPAL_GEOMETRY_CACHE_EQUIVALENCE checks="+checks+" exactFloatBits=true loadCutSeparateRejectReset=true committedHistoryCompared=true headset=false");
        }
        static bool Exact(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        static bool Exact(Vector3 a,Vector3 b)=>Exact(a.x,b.x)&&Exact(a.y,b.y)&&Exact(a.z,b.z);
        static bool Exact(TissueTensor a,TissueTensor b)=>Exact(a.x,b.x)&&Exact(a.y,b.y)&&Exact(a.z,b.z);
        static void CompareExact(TissueVolume r,TissueVolume c,string label)
        {
            Require(r.NodeCount==c.NodeCount&&r.CutFaceCount==c.CutFaceCount&&r.SeparatedFaceCount==c.SeparatedFaceCount&&r.TopologyRevision==c.TopologyRevision,label+" exact topology counts");
            Require(r.LastStepAccepted==c.LastStepAccepted&&r.AcceptedStepSequence==c.AcceptedStepSequence&&r.LastStepRetries==c.LastStepRetries&&
                r.LastStepBacktracks==c.LastStepBacktracks&&r.LastRejectedCell==c.LastRejectedCell&&Exact(r.LastRejectedJacobian,c.LastRejectedJacobian)&&
                r.MaterialHandleCount==c.MaterialHandleCount&&r.HasAcceptedStep==c.HasAcceptedStep,label+" exact acceptance/history counters");
            Require(!(bool)CacheValid.GetValue(c)&&!(bool)CacheValid.GetValue(r),label+" no evaluated geometry survives its trial");
            var rf=new Vector3[r.NodeCount];var cf=new Vector3[c.NodeCount];r.MeasureNodalForces(rf);c.MeasureNodalForces(cf);
            for(int node=0;node<r.NodeCount;node++)
            {
                Require(Exact(r.Positions[node],c.Positions[node]),label+" exact accepted position "+node);
                Require(Exact(rf[node],cf[node]),label+" exact nodal force "+node);
                Require(Exact(r.NodeMass(node),c.NodeMass(node)),label+" exact active mass "+node);
            }
            for(int face=0;face<r.Faces.Length;face++)Require(r.Faces[face].cut==c.Faces[face].cut&&r.Faces[face].separated==c.Faces[face].separated,label+" exact face state");
            var rh=(MaxwellHistory[])Histories.GetValue(r);var ch=(MaxwellHistory[])Histories.GetValue(c);
            for(int cell=0;cell<r.Cells.Length;cell++)
            {
                for(int corner=0;corner<4;corner++)Require(r.NodeFor(cell,corner)==c.NodeFor(cell,corner),label+" exact corner binding");
                Require(rh[cell].HasHistory==ch[cell].HasHistory&&Exact((TissueTensor)PreviousStrain.GetValue(rh[cell]),(TissueTensor)PreviousStrain.GetValue(ch[cell])),label+" exact committed previous strain");
                var rv=(TissueTensor[])ViscousStrain.GetValue(rh[cell]);var cv=(TissueTensor[])ViscousStrain.GetValue(ch[cell]);
                Require(rv.Length==cv.Length,label+" same material branch count");
                for(int branch=0;branch<rv.Length;branch++)Require(Exact(rv[branch],cv[branch]),label+" exact committed viscous memory");
            }
        }
        static double Step(TissueVolume volume)
        {long at=System.Diagnostics.Stopwatch.GetTimestamp();volume.Step(1f/90,Vector3.zero,Vector3.zero);
            return (System.Diagnostics.Stopwatch.GetTimestamp()-at)*1000d/System.Diagnostics.Stopwatch.Frequency;}
        static double P95(List<double> values)
        {var sorted=values.Skip(5).OrderBy(x=>x).ToArray();return sorted[(int)((sorted.Length-1)*.95)];}
        static void Compare(TissueVolume r,TissueVolume b,string label)
        {
            Require(r.NodeCount==b.NodeCount&&r.CutFaceCount==b.CutFaceCount&&r.TopologyRevision==b.TopologyRevision,label+" topology agrees");
            Require(r.LastStepAccepted==b.LastStepAccepted&&r.AcceptedStepSequence==b.AcceptedStepSequence&&r.MaterialHandleCount==b.MaterialHandleCount,label+" acceptance and grips agree");
            var rf=new Vector3[r.NodeCount];var bf=new Vector3[b.NodeCount];r.MeasureNodalForces(rf);b.MeasureNodalForces(bf);
            float referenceNorm=0,differenceNorm=0,maxDifference=0;
            for(int i=0;i<r.NodeCount;i++){referenceNorm+=rf[i].sqrMagnitude;differenceNorm+=(rf[i]-bf[i]).sqrMagnitude;maxDifference=Mathf.Max(maxDifference,(rf[i]-bf[i]).magnitude);}
            Debug.Log($"SCALPAL_VOLUME_ACCELERATION_DIFFERENCE {label} forceNorm={Mathf.Sqrt(referenceNorm):F6} differenceNorm={Mathf.Sqrt(differenceNorm):F6} maximumDifference={maxDifference:F6}");
            for(int i=0;i<r.NodeCount;i++)
            {Require((r.Positions[i]-b.Positions[i]).magnitude<.00005f,label+" accepted node position "+i);
                Require((rf[i]-bf[i]).magnitude<=.001f+.005f*rf[i].magnitude,label+" material nodal force "+i+" reference="+rf[i]+" Burst="+bf[i]+" delta="+(rf[i]-bf[i]).magnitude);}
        }
    }
}
