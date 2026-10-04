using System;
using System.Collections.Generic;
using Scalpal.Exercises.Engine;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Surgery
{
    // Original synthesized effects driven only by accepted body records. Place this component at
    // the wound for spatial audio. The caller supplies the held controller; no hand is guessed.
    [DisallowMultipleComponent]
    public sealed class SurgeryFeedback : MonoBehaviour
    {
        [Range(0,1)] public float volume = .22f;
        public bool haptics = true;
        readonly HashSet<string> played = new HashSet<string>();
        AudioSource source;
        AudioClip blade, clamp, suction;
        float lastBlade = -1, lastSuction = -1;
        void Awake() => Initialize();
        void Initialize()
        {
            if (source) return;
            source = gameObject.AddComponent<AudioSource>(); source.playOnAwake = false;
            source.spatialBlend = .85f; source.minDistance = .25f; source.maxDistance = 4;
            source.rolloffMode = AudioRolloffMode.Linear;
            blade = Synthesize("OriginalBladeStroke", .09f, 0);
            clamp = Synthesize("OriginalClampRatchet", .10f, 1);
            suction = Synthesize("OriginalSuctionSlurp", .24f, 2);
        }
        public void ResetHistory()
        {
            played.Clear(); lastBlade = lastSuction = -1; if (source) source.Stop();
        }
        public void Play(BodyRecord record, XRNode? hand = null)
        {
            if (record?.action == null || !record.action.registered || !played.Add(record.action.actionId)) return;
            Initialize();
            bool denied = Has(record,"not_exposed") || Has(record,"not_cuttable") || Has(record,"missing_instance");
            bool injury = Has(record,"cut_unsecured") || Has(record,"hollow_leak") || Has(record,"critical_injury") || Has(record,"untented_cut") || Has(record,"muscle_cut");
            float amplitude = denied ? .06f : injury ? .38f : .12f, duration = injury ? .10f : .035f;
            AudioClip clip = null;
            if (!denied)
            {
                float now = Time.unscaledTime;
                switch (record.action.verb)
                {
                    case "cut": if (now-lastBlade >= .08f) { clip=blade; lastBlade=now; } break;
                    case "clamp": case "tie": clip=clamp; amplitude=Mathf.Max(amplitude,.22f); break;
                    case "suction": if (now-lastSuction >= .20f) { clip=suction; lastSuction=now; } break;
                }
            }
            if (clip) source.PlayOneShot(clip, volume);
            if (haptics && hand.HasValue && (clip || injury || denied))
            {
                var device = InputDevices.GetDeviceAtXRNode(hand.Value);
                if (device.isValid && device.TryGetHapticCapabilities(out var capability) && capability.supportsImpulse)
                    device.SendHapticImpulse(0, amplitude, duration);
            }
        }
        static bool Has(BodyRecord record, string outcome) => record.outcomes != null && Array.IndexOf(record.outcomes,outcome) >= 0;
        static AudioClip Synthesize(string name, float duration, int kind)
        {
            const int sampleRate = 24000;
            var samples = new float[Mathf.CeilToInt(duration*sampleRate)];
            uint noise = 0x62483u; float filtered = 0;
            for (int i=0;i<samples.Length;i++)
            {
                noise = unchecked(noise*1664525u+1013904223u);
                float white = ((noise>>8)/(float)0xffffff)*2-1;
                float t=i/(float)sampleRate, envelope=Mathf.Sin(Mathf.PI*i/(samples.Length-1));
                filtered = Mathf.Lerp(filtered,white,.13f);
                if (kind == 0) samples[i] = (white-filtered)*envelope*.30f;
                else if (kind == 1)
                {
                    float pulseTime=t%.027f;
                    float ratchet=Mathf.Exp(-pulseTime*320)*Mathf.Sin(t*2*Mathf.PI*1800);
                    samples[i]=(ratchet*.6f+white*Mathf.Exp(-pulseTime*500)*.20f)*envelope;
                }
                else samples[i] = (filtered*.7f+Mathf.Sin(t*2*Mathf.PI*(95+25*Mathf.Sin(t*21)))*.13f)*envelope;
            }
            var clip=AudioClip.Create(name,samples.Length,1,sampleRate,false); clip.SetData(samples,0); return clip;
        }
        static void Release(UnityEngine.Object value){if(!value)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        void OnDestroy()
        {
            Release(blade); Release(clamp); Release(suction);
        }
    }
}
