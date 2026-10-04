using System;
using System.Collections.Generic;
using System.Linq;
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
