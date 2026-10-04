using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
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
        sealed class Source { public string id; public VesselBleeding fluid = new VesselBleeding(); public bool initialized; public BodyAction published; }
        readonly List<Source> sources = new List<Source>();
        OpenBodyInteraction input;
        AnatomyExerciseBinding exercise;
        Transform wound;
        GameObject pool;
        Material material;
        float snapshotClock;
        // Pause simulation/interaction independently of drawing existing blood during inspection.
        // A lost patient registration still hides the field; it never deletes accumulated fluid.
        public Func<bool> PresentationVisible;
        readonly Dictionary<InstrumentBehaviour,float> suctionTimes = new Dictionary<InstrumentBehaviour,float>();
        public double PoolMl { get; private set; }
        // The visible pool follows the reducer's poolMl (BodyState), eased so its 1 Hz fluid snapshots do not jump.
        public double ShownPoolMl { get; private set; }
        // Depth of blood in the wound cavity: the pool spreads to its 4 cm cap, then fills toward the opening.
        public float PoolLevelMeters { get; private set; }
        public void Initialize(OpenBodyInteraction adapter, AnatomyExerciseBinding binding, Transform woundFrame)
        {
            if (input) input.Submitted -= Applied;
            input = adapter; exercise = binding; wound = woundFrame;
            sources.Clear(); suctionTimes.Clear(); snapshotClock = 0; PoolMl = ShownPoolMl = 0; PoolLevelMeters = 0;
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
            string verb = record.action.verb;
            if (verb == "cut" || verb == "clamp" || verb == "release" || verb == "tie" || verb == "seal")
                foreach (var source in sources)
                {
                    if (source.id != record.action.tissueId) continue;
                    // The body decides positionally whether each injury is controlled; the vessel follows it.
                    bool open = exercise.Body.Get(source.id, "openInjuries") > 0;
                    if (open) source.fluid.OpenInjury(.000000785398);
                    source.fluid.SetOccluded(!open);
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
            { if(pool)pool.SetActive(PresentationVisible!=null&&PresentationVisible()&&ShownPoolMl>.001); suctionTimes.Clear(); return; }
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
            // Measured fluid is telemetry, not a scored action: at most 1 Hz and only when a value changed.
            snapshotClock += seconds; bool publish = snapshotClock >= 1;
            if (publish) snapshotClock %= 1;
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
            double target = Math.Max(0, exercise.Body.Get("", "poolMl"));
            ShownPoolMl += (target - ShownPoolMl) * (1 - Math.Exp(-seconds / .35));
            if (Math.Abs(target - ShownPoolMl) < .001) ShownPoolMl = target;
            pool.SetActive(ShownPoolMl > .001);
            // Flattened ellipsoid of the shown volume: V = 2/3 pi r^2 h, at least 3 mm deep, at most the 25 mm cavity.
            float radius = Mathf.Min(.04f,Mathf.Sqrt((float)(3 * ShownPoolMl * 1e-6/(2*Math.PI*.003))));
            float level = radius > 0 ? Mathf.Clamp((float)(3 * ShownPoolMl * 1e-6/(2*Math.PI*radius*radius)), .003f, .025f) : 0;
            PoolLevelMeters = ShownPoolMl > .001 ? level : 0;
            pool.transform.localPosition = new Vector3(0,0,.025f-(level-.003f)*.5f);
            pool.transform.localScale = new Vector3(radius*2,radius*2,Mathf.Max(level,.003f));
        }
        bool Publish(Source source)
        {
            var action = input.CreateMeasurement("assistant","fluid",source.id,wound.position,"vessel-model");
            if (action == null) return false;
            action.bloodLostMl = source.fluid.CumulativeLossMilliliters;
            action.poolMl = source.fluid.PooledMilliliters;
            action.flowMlPerSecond = source.fluid.FlowMillilitersPerSecond;
            action.Quantize();
            var last = source.published;
            if (last != null && last.bloodLostMl == action.bloodLostMl && last.poolMl == action.poolMl && last.flowMlPerSecond == action.flowMlPerSecond) return true;
            if (!input.SubmitMeasured(action)) return false;
            source.published = action.Copy(); return true;
        }
        static void Release(UnityEngine.Object value){if(!value)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        void OnDisable() { if(pool)pool.SetActive(false); }
        void OnDestroy() { if(input)input.Submitted-=Applied;if(pool)Release(pool);if(material)Release(material); }
    }
}
