using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Shell
{
    // One opaque instanced draw. Petals stay behind the 1.3 m UI plane; no transparency stacks.
    public sealed class HubBlossoms : MonoBehaviour
    {
        public Mesh petal;
        public Material material;
        const int Count = 28;
        readonly Matrix4x4[] poses = new Matrix4x4[Count];
        readonly Vector3[] anchors = new Vector3[Count];
        readonly Vector3[] angles = new Vector3[Count];
        readonly Vector3[] scales = new Vector3[Count];
        readonly float[] phases = new float[Count];
        bool initialized;

        void OnEnable()
        {
            if (initialized) return;
            initialized = true;
            for (int i = 0; i < Count; i++)
            {
                float phase = i * 2.399963f;
                phases[i] = phase;
                // Sparse floral curtains flank the cards, with occasional petals behind the grid.
                float side = i % 2 == 0 ? -1 : 1;
                anchors[i] = new Vector3(side*(.64f+(i%5)*.18f), -.85f+(i%9)*.22f, 1.85f+(i%4)*.27f);
                angles[i] = new Vector3(i*19,i*31,i*13);
                scales[i] = new Vector3(.026f,.006f,.043f)*(1+(i%3)*.12f);
            }
        }
        void Update()
        {
            if (!petal || !material || !material.enableInstancing || !SystemInfo.supportsInstancing) return;
            // Scaled time deliberately freezes decorative motion with the shared pause menu.
            float time = Time.time;
            var rotation = transform.rotation;
            for (int i = 0; i < Count; i++)
            {
                float phase = phases[i];
                var drift = new Vector3(Mathf.Sin(time*.12f+phase)*.065f,
                    Mathf.Sin(time*.095f+phase)*.085f, Mathf.Cos(time*.08f+phase)*.025f);
                var tumble = angles[i] + new Vector3(Mathf.Sin(time*.13f+phase)*12,time*2.1f,Mathf.Sin(time*.09f+phase)*9);
                poses[i] = Matrix4x4.TRS(transform.TransformPoint(anchors[i]+drift), rotation*Quaternion.Euler(tumble), scales[i]);
            }
            Graphics.DrawMeshInstanced(petal,0,material,poses,Count,null,ShadowCastingMode.Off,false,
                gameObject.layer,null,LightProbeUsage.Off,null);
        }
    }
}
