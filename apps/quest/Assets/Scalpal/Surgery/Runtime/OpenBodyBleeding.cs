using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Existing pressure/volume-conserving vessel model supplies measured fluid totals to the
    // same scored event path. Semantic rates are disabled per tissue after the initial snapshot.
    public sealed class OpenBodyBleeding : MonoBehaviour
    {
        sealed class Source { public string id; public VesselBleeding fluid = new VesselBleeding(); public bool initialized; }
        readonly List<Source> sources = new List<Source>();
        OpenBodyInteraction input;
        AnatomyExerciseBinding exercise;
        Transform wound;
        GameObject pool;
        Material material;
        float snapshotClock;
        readonly Dictionary<InstrumentBehaviour,float> suctionTimes = new Dictionary<InstrumentBehaviour,float>();
        public double PoolMl { get; private set; }
        public void Initialize(OpenBodyInteraction adapter, AnatomyExerciseBinding binding, Transform woundFrame)
        {
            if (input) input.Submitted -= Applied;
            input = adapter; exercise = binding; wound = woundFrame;
            sources.Clear(); suctionTimes.Clear(); snapshotClock = 0; PoolMl = 0;
            foreach (var tissue in binding.Body.Tissues) if (tissue.perfused) sources.Add(new Source { id = tissue.id });
            input.Submitted += Applied;
            if (!pool)
            {
                pool = GameObject.CreatePrimitive(PrimitiveType.Sphere); pool.name = "ScoredTeachingBloodPool";
                Release(pool.GetComponent<Collider>());
                material = TissueRuntimeMaterial.Create("ScoredBlood",new Color(.3f,.003f,.008f));
                material.SetFloat("_Glossiness",.9f); pool.GetComponent<Renderer>().sharedMaterial = material;
            }
            pool.transform.SetParent(wound,false); pool.SetActive(false);
        }
        void Applied(BodyRecord record, InstrumentBehaviour tool)
        {
            if (record.action.verb == "fluid" || Array.IndexOf(record.outcomes,"not_exposed") >= 0) return;
            foreach (var source in sources)
            {
                if (source.id != record.action.tissueId) continue;
                if (record.action.verb == "cut" && Array.IndexOf(record.outcomes,"cut_unsecured") >= 0)
                { source.fluid.OpenInjury(.000000785398); source.fluid.SetOccluded(false); }
                if (record.action.verb == "clamp" || record.action.verb == "tie" || record.action.verb == "seal") source.fluid.SetOccluded(true);
            }
            if (record.action.verb == "suction" && record.action.choice == "pool_suction" && pool && pool.activeSelf)
            {
                // Require physical tip proximity to the visible blood pool, not a remote target dwell.
                var point = pool.transform.InverseTransformPoint(wound.parent.TransformPoint(new Vector3(record.action.position.x,record.action.position.y,record.action.position.z)));
                if (point.sqrMagnitude <= .36f)
                {
                    double amount = record.action.durationMs * .01;
                    foreach (var source in sources) amount -= source.fluid.RemovePool(Math.Max(0,amount));
                }
            }
        }
        public void Prime()
        {
            if(!input || !input.Ready)return;
            foreach(var source in sources) if(!source.initialized)source.initialized=Publish(source);
        }
        public void Simulate(float seconds)
        {
            if (!input || !input.Ready || !float.IsFinite(seconds) || seconds <= 0 || seconds > .1f)
            { if(pool)pool.SetActive(false); suctionTimes.Clear(); return; }
            var rig = GetComponent<NativeCaseSession>()?.workbench;
            foreach(var tool in rig ? rig.tools : Array.Empty<InstrumentBehaviour>())
            {
                if(!tool || tool.instrumentId!="suction_irrigator")continue;
                if(!tool.Held||!tool.TrackingValid||tool.Activation<.7f||!tool.actionPoint||!pool||!pool.activeSelf||pool.transform.InverseTransformPoint(tool.actionPoint.position).sqrMagnitude>.36f)
                {suctionTimes.Remove(tool);continue;}
                suctionTimes.TryGetValue(tool,out float duration);duration+=seconds; suctionTimes[tool]=duration;
                if(duration<.2f||sources.Count==0)continue;
                var suction=input.CreateMeasurement(tool.instrumentId,"suction",sources[0].id,tool.actionPoint.position,"pool-tool-"+Math.Abs(tool.GetInstanceID()));
                if(suction!=null){suction.durationMs=duration*1000;suction.choice="pool_suction";input.SubmitMeasured(suction);} suctionTimes[tool]=0;
            }
            snapshotClock += seconds; bool publish = snapshotClock >= .25f;
            if (publish) snapshotClock %= .25f;
            PoolMl = 0;
            foreach (var source in sources)
            {
                // Publish zero before any interaction can injure this source, avoiding two ledgers.
                if (!source.initialized) source.initialized = Publish(source);
                if (!source.initialized) continue;
                source.fluid.Step(seconds); PoolMl += source.fluid.PooledMilliliters;
                if (publish) Publish(source);
            }
            if (!pool) return;
            pool.SetActive(PoolMl > .001);
            float radius = Mathf.Min(.04f,Mathf.Sqrt((float)(3 * PoolMl * 1e-6/(2*Math.PI*.003))));
            pool.transform.localPosition = new Vector3(0,0,.025f);
            pool.transform.localScale = new Vector3(radius*2,radius*2,.003f);
        }
        bool Publish(Source source)
        {
            var action = input.CreateMeasurement("assistant","fluid",source.id,wound.position,"vessel-model");
            if (action == null) return false;
            action.bloodLostMl = (float)source.fluid.CumulativeLossMilliliters;
            action.poolMl = (float)source.fluid.PooledMilliliters;
            action.flowMlPerSecond = (float)source.fluid.FlowMillilitersPerSecond;
            return input.SubmitMeasured(action);
        }
        static void Release(UnityEngine.Object value){if(!value)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        void OnDisable() { if(pool)pool.SetActive(false); }
        void OnDestroy() { if(input)input.Submitted-=Applied;if(pool)Release(pool);if(material)Release(material); }
    }
}
