using UnityEngine;

namespace Scalpal.Shell
{
    // One instanced opaque draw for a deliberately sparse petal field; no transparency stacks.
    public sealed class HubBlossoms : MonoBehaviour
    {
        public Mesh petal;
        public Material material;
        readonly Matrix4x4[] poses = new Matrix4x4[40];
        void Update()
        {
            if (!petal || !material || !SystemInfo.supportsInstancing) return;
            for (int i = 0; i < poses.Length; i++)
            {
                float seed = i * 2.399963f, t = Time.time * .09f;
                var p = new Vector3(Mathf.Cos(seed) * (1.4f + i % 3 * .3f), .35f + i % 8 * .34f + Mathf.Sin(t + seed) * .09f, 2.1f + Mathf.Sin(seed) * .7f);
                poses[i] = Matrix4x4.TRS(transform.TransformPoint(p), Quaternion.Euler(i * 19, i * 31 + t * 20, i * 13), new Vector3(.026f,.007f,.047f));
            }
            Graphics.DrawMeshInstanced(petal, 0, material, poses, poses.Length, null, UnityEngine.Rendering.ShadowCastingMode.Off, false);
        }
    }
}
